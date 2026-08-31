using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Identity;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// User CRUD use cases (BG-005a): CreateUser (admin-only), SetUserActive /
/// Deactivate (admin-only), ChangePassword (admin any user, any user
/// themselves). Runs store-agnostically against the in-memory store double;
/// every failure asserts the Result/DomainError shape.
///
/// F8 actor binding (BG-005d): the acting user comes from the SESSION
/// <see cref="IActorContext"/> (an in-memory <see cref="ActorContext"/> here —
/// the same seam the login path uses), and every command's By must equal the
/// signed-in actor: no session -> AUTH_ACTOR_NOT_SIGNED_IN, spoofed By ->
/// AUTH_ACTOR_MISMATCH, both before any store lookup.
/// </summary>
public sealed class UserAccountServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash) => hash == $"hash:{password}";
    }

    /// <summary>Delegating store double that forces the active-admin count (F6 last-admin guard test).</summary>
    private sealed class ForcedActiveAdminCountStore(IUserAccountStore inner, int count) : IUserAccountStore
    {
        public Task<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken = default) =>
            inner.FindByIdAsync(id, cancellationToken);

        public Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            inner.FindByUsernameAsync(username, cancellationToken);

        public Task AddAsync(User user, CancellationToken cancellationToken = default) =>
            inner.AddAsync(user, cancellationToken);

        public Task UpdateAsync(User user, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(user, cancellationToken);

        public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(count);
    }

    private readonly InMemoryUserAccountStore _store = new();
    private readonly ActorContext _actorContext = new();
    private readonly UserAccountService _service;

    public UserAccountServiceTests()
    {
        // The session seam: tests that do not run a login round-trip set the
        // actor directly on the SAME IActorContext surface the host wiring
        // (LoginService set-on-success) uses.
        _service = new UserAccountService(_store, new FakeHasher(), _actorContext);
    }

    /// <summary>Signs the given account into the session context.</summary>
    private void SignIn(User user) => _actorContext.Set(user.Id);

    private static User Actor(Role role, UserId? id = null) => new()
    {
        Id = id ?? new UserId(Guid.NewGuid()),
        Username = $"actor-{role}",
        PasswordHash = "hash:secret",
        Role = role,
        CreatedBy = UserId.Anonymous,
        CreatedUtc = T0,
    };

    private static CreateUserCommand NewCreate(UserId by, Role role = Role.Supervisor) =>
        new(new UserId(Guid.NewGuid()), "new.user", "pw", role, by, T0);

    [Fact]
    public async Task Admin_CreateUser_ReturnsHashedActiveUser()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        SignIn(admin);
        var cmd = new CreateUserCommand(
            new UserId(Guid.NewGuid()), "  alice  ", "s3cret", Role.Supervisor,
            admin.Id, T0);

        Result<User> result = await _service.CreateUserAsync(cmd);

        Assert.True(result.IsSuccess);
        User created = result.Value;
        Assert.Equal("alice", created.Username);          // trimmed
        Assert.Equal("hash:s3cret", created.PasswordHash); // hashed, never plaintext
        Assert.Equal(Role.Supervisor, created.Role);
        Assert.Equal(0, created.FailedAttempts);
        Assert.Null(created.LockedUntilUtc);
        Assert.Null(created.DeactivatedUtc);
        Assert.Equal(admin.Id, created.CreatedBy);
        Assert.Equal(T0, created.CreatedUtc);
        Assert.Equal(2, _store.Users.Count); // the actor + the created user
    }

    [Theory]
    [InlineData(Role.Supervisor)]
    public async Task NonAdmin_CreateUser_RejectedForbidden(Role role)
    {
        User actor = Actor(role);
        await _store.AddAsync(actor);
        SignIn(actor);

        Result<User> result = await _service.CreateUserAsync(NewCreate(actor.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Errors[0].Code);
        Assert.Single(_store.Users); // only the actor; nothing new persisted
    }

    [Fact]
    public async Task UnknownActor_CreateUser_Rejected()
    {
        var unknown = new UserId(Guid.NewGuid());
        _actorContext.Set(unknown); // the session says an unknown id is signed in

        Result<User> result = await _service.CreateUserAsync(NewCreate(unknown));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_UNKNOWN", result.Errors[0].Code);
    }

    [Fact]
    public async Task DeactivatedActor_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin) with { DeactivatedUtc = T0 };
        await _store.AddAsync(admin);
        SignIn(admin);

        Result<User> result = await _service.CreateUserAsync(NewCreate(admin.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_DEACTIVATED", result.Errors[0].Code);
    }

    [Fact]
    public async Task DuplicateUsername_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        await _store.AddAsync(Actor(Role.Supervisor) with { Username = "taken" });
        SignIn(admin);

        Result<User> result = await _service.CreateUserAsync(NewCreate(admin.Id) with { Username = "taken" });

        Assert.True(result.IsFailure);
        Assert.Equal("DUP_USERNAME", result.Errors[0].Code);
    }

    [Fact]
    public async Task BlankUsernameOrPassword_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        SignIn(admin);

        Result<User> blankUser = await _service.CreateUserAsync(NewCreate(admin.Id) with { Username = "  " });
        Result<User> blankPass = await _service.CreateUserAsync(NewCreate(admin.Id) with { Password = "" });

        Assert.Equal("INVALID_USERNAME", blankUser.Errors[0].Code);
        Assert.Equal("INVALID_PASSWORD", blankPass.Errors[0].Code);
    }

    [Fact]
    public async Task Admin_Deactivate_SetsDeactivatedUtc_AndReactivateClearsIt()
    {
        User admin = Actor(Role.Admin);
        User target = Actor(Role.Supervisor) with { FailedAttempts = 3, LockedUntilUtc = T0 };
        await _store.AddAsync(admin);
        await _store.AddAsync(target);
        SignIn(admin);

        Result<User> deactivated = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: false, admin.Id, T0));
        Assert.True(deactivated.IsSuccess);
        Assert.Equal(T0, deactivated.Value.DeactivatedUtc);
        Assert.Equal(3, deactivated.Value.FailedAttempts); // deactivation itself leaves auth state

        Result<User> reactivated = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: true, admin.Id, T0.AddHours(1)));
        Assert.True(reactivated.IsSuccess);
        Assert.Null(reactivated.Value.DeactivatedUtc);
        Assert.Equal(0, reactivated.Value.FailedAttempts);    // reactivation clears auth state (F6)
        Assert.Null(reactivated.Value.LockedUntilUtc);
    }

    [Fact]
    public async Task Admin_DeactivateSelf_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        SignIn(admin);

        Result<User> result = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(admin.Id, Active: false, admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_CANNOT_DEACTIVATE_SELF", result.Errors[0].Code);
        Assert.Null((await _store.FindByIdAsync(admin.Id))!.DeactivatedUtc); // nothing happened
    }

    [Fact]
    public async Task DeactivateLastActiveAdmin_Rejected()
    {
        // The store reports only ONE active admin (F6 defensive guard: with a
        // live count == 1, the last active admin can never be deactivated).
        var store = new ForcedActiveAdminCountStore(_store, 1);
        var service = new UserAccountService(store, new FakeHasher(), _actorContext);
        User actor = Actor(Role.Admin);
        User otherAdmin = Actor(Role.Admin);
        await _store.AddAsync(actor);
        await _store.AddAsync(otherAdmin);
        SignIn(actor);

        Result<User> result = await service.SetUserActiveAsync(
            new SetUserActiveCommand(otherAdmin.Id, Active: false, actor.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_NEEDS_ADMIN", result.Errors[0].Code);
    }

    [Fact]
    public async Task Admin_DeactivateAnotherAdmin_WhenTwoActiveAdmins_Succeeds()
    {
        User actor = Actor(Role.Admin);
        User otherAdmin = Actor(Role.Admin);
        await _store.AddAsync(actor);
        await _store.AddAsync(otherAdmin);
        SignIn(actor);

        Result<User> result = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(otherAdmin.Id, Active: false, actor.Id, T0));

        Assert.True(result.IsSuccess);
        Assert.Equal(T0, result.Value.DeactivatedUtc);
    }

    [Fact]
    public async Task NonAdmin_Deactivate_RejectedForbidden()
    {
        User supervisor = Actor(Role.Supervisor);
        User target = Actor(Role.Supervisor);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(target);
        SignIn(supervisor);

        Result<User> result = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: false, supervisor.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Errors[0].Code);
    }

    [Fact]
    public async Task Admin_ChangePassword_ForAnotherUser_Succeeds()
    {
        User admin = Actor(Role.Admin);
        User target = Actor(Role.Supervisor);
        await _store.AddAsync(admin);
        await _store.AddAsync(target);
        SignIn(admin);

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(target.Id, "new-secret", admin.Id, T0));

        Assert.True(result.IsSuccess);
        Assert.Equal("hash:new-secret", result.Value.PasswordHash);
        Assert.Equal(target.Id, result.Value.Id);
    }

    [Fact]
    public async Task User_ChangeOwnPassword_Succeeds()
    {
        User supervisor = Actor(Role.Supervisor);
        await _store.AddAsync(supervisor);
        SignIn(supervisor);

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(supervisor.Id, "own-secret", supervisor.Id, T0));

        Assert.True(result.IsSuccess);
        Assert.Equal("hash:own-secret", result.Value.PasswordHash);
    }

    [Fact]
    public async Task Supervisor_ChangeAnothersPassword_RejectedForbidden()
    {
        User supervisor = Actor(Role.Supervisor);
        User target = Actor(Role.Supervisor);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(target);
        SignIn(supervisor);

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(target.Id, "pw", supervisor.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Errors[0].Code);
        Assert.Equal("hash:secret", (await _store.FindByIdAsync(target.Id))!.PasswordHash); // unchanged
    }

    [Fact]
    public async Task ChangePassword_UnknownTarget_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        SignIn(admin);

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(new UserId(Guid.NewGuid()), "pw", admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("USER_UNKNOWN", result.Errors[0].Code);
    }

    // --- F8 actor binding (BG-005d) ---

    [Fact]
    public async Task NoSession_AnyCommand_RejectedNotSignedIn_BeforeAnyStoreLookup()
    {
        // A brand-new host context has no signed-in actor: the use case must
        // refuse BEFORE the offending By could be resolved or persisted.
        Result<User> create = await _service.CreateUserAsync(NewCreate(new UserId(Guid.NewGuid())));
        Result<User> change = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(new UserId(Guid.NewGuid()), "pw", new UserId(Guid.NewGuid()), T0));

        Assert.Equal("AUTH_ACTOR_NOT_SIGNED_IN", create.Errors[0].Code);
        Assert.Equal("AUTH_ACTOR_NOT_SIGNED_IN", change.Errors[0].Code);
        Assert.Empty(_store.Users);
    }

    [Fact]
    public async Task SpoofedAdminBy_CreateUser_RejectedActorMismatch()
    {
        // A non-admin session passes cmd.By = the ADMIN's id: the gate must
        // reject on the actor binding, before the role check and before any
        // lookup of the admin, so the spoof can never reach the store.
        User attacker = Actor(Role.Supervisor);
        User admin = Actor(Role.Admin);
        await _store.AddAsync(attacker);
        await _store.AddAsync(admin);
        SignIn(attacker);

        Result<User> result = await _service.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "spoofed", "pw", Role.Admin, admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_MISMATCH", result.Errors[0].Code);
        Assert.Equal(2, _store.Users.Count); // nothing created
    }

    [Fact]
    public async Task SpoofedAdminBy_SetUserActive_RejectedActorMismatch()
    {
        User supervisor = Actor(Role.Supervisor);
        User admin = Actor(Role.Admin);
        User target = Actor(Role.Supervisor);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(admin);
        await _store.AddAsync(target);
        SignIn(supervisor);

        // The supervisor tries to deactivate acting as the admin.
        Result<User> result = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: false, admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_MISMATCH", result.Errors[0].Code);
        Assert.Null((await _store.FindByIdAsync(target.Id))!.DeactivatedUtc); // untouched
    }

    [Fact]
    public async Task SpoofedAdminBy_ChangePassword_RejectedActorMismatch()
    {
        User supervisor = Actor(Role.Supervisor);
        User admin = Actor(Role.Admin);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(admin);
        SignIn(supervisor);

        // The supervisor tries to reset a password posing as the admin.
        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(admin.Id, "new-admin-pass", admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_MISMATCH", result.Errors[0].Code);
        Assert.Equal("hash:secret", (await _store.FindByIdAsync(admin.Id))!.PasswordHash); // untouched
    }

    [Fact]
    public async Task ActorContext_Clear_RemovesTheSessionActor()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        SignIn(admin);
        Assert.Equal(admin.Id, _actorContext.CurrentUserId);

        _actorContext.Clear(); // the future sign-out path (BG-005d-ui)

        Assert.Null(_actorContext.CurrentUserId);
        Result<User> result = await _service.CreateUserAsync(NewCreate(admin.Id));
        Assert.Equal("AUTH_ACTOR_NOT_SIGNED_IN", result.Errors[0].Code); // cleared session refuses
    }
}
