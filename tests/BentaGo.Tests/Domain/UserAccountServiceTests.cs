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
/// </summary>
public sealed class UserAccountServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"hash:{password}";
        public bool Verify(string password, string hash) => hash == $"hash:{password}";
    }

    private readonly InMemoryUserAccountStore _store = new();
    private readonly UserAccountService _service;

    public UserAccountServiceTests()
    {
        _service = new UserAccountService(_store, new FakeHasher());
    }

    private static User Actor(Role role, UserId? id = null) => new()
    {
        Id = id ?? new UserId(Guid.NewGuid()),
        Username = $"actor-{role}",
        PasswordHash = "hash:secret",
        Role = role,
        CreatedBy = UserId.Anonymous,
        CreatedUtc = T0,
    };

    private static CreateUserCommand NewCreate(UserId by, Role role = Role.Peddler) =>
        new(new UserId(Guid.NewGuid()), "new.user", "pw", role, PeddlerLink: null, by, T0);

    [Fact]
    public async Task Admin_CreateUser_ReturnsHashedActiveUser()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        var cmd = new CreateUserCommand(
            new UserId(Guid.NewGuid()), "  alice  ", "s3cret", Role.Peddler,
            new PeddlerId(Guid.NewGuid()), admin.Id, T0);

        Result<User> result = await _service.CreateUserAsync(cmd);

        Assert.True(result.IsSuccess);
        User created = result.Value;
        Assert.Equal("alice", created.Username);          // trimmed
        Assert.Equal("hash:s3cret", created.PasswordHash); // hashed, never plaintext
        Assert.Equal(Role.Peddler, created.Role);
        Assert.Equal(0, created.FailedAttempts);
        Assert.Null(created.LockedUntilUtc);
        Assert.Null(created.DeactivatedUtc);
        Assert.Equal(admin.Id, created.CreatedBy);
        Assert.Equal(T0, created.CreatedUtc);
        Assert.Single(_store.Users);
    }

    [Theory]
    [InlineData(Role.Supervisor)]
    [InlineData(Role.Peddler)]
    public async Task NonAdmin_CreateUser_RejectedForbidden(Role role)
    {
        User actor = Actor(role);
        await _store.AddAsync(actor);

        Result<User> result = await _service.CreateUserAsync(NewCreate(actor.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Errors[0].Code);
        Assert.Empty(_store.Users); // nothing persisted
    }

    [Fact]
    public async Task UnknownActor_CreateUser_Rejected()
    {
        Result<User> result = await _service.CreateUserAsync(NewCreate(new UserId(Guid.NewGuid())));
        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_UNKNOWN", result.Errors[0].Code);
    }

    [Fact]
    public async Task DeactivatedActor_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin) with { DeactivatedUtc = T0 };
        await _store.AddAsync(admin);

        Result<User> result = await _service.CreateUserAsync(NewCreate(admin.Id));

        Assert.True(result.IsFailure);
        Assert.Equal("AUTH_ACTOR_DEACTIVATED", result.Errors[0].Code);
    }

    [Fact]
    public async Task DuplicateUsername_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);
        await _store.AddAsync(Actor(Role.Peddler) with { Username = "taken" });

        Result<User> result = await _service.CreateUserAsync(NewCreate(admin.Id) with { Username = "taken" });

        Assert.True(result.IsFailure);
        Assert.Equal("DUP_USERNAME", result.Errors[0].Code);
    }

    [Fact]
    public async Task BlankUsernameOrPassword_CreateUser_Rejected()
    {
        User admin = Actor(Role.Admin);
        await _store.AddAsync(admin);

        Result<User> blankUser = await _service.CreateUserAsync(NewCreate(admin.Id) with { Username = "  " });
        Result<User> blankPass = await _service.CreateUserAsync(NewCreate(admin.Id) with { Password = "" });

        Assert.Equal("INVALID_USERNAME", blankUser.Errors[0].Code);
        Assert.Equal("INVALID_PASSWORD", blankPass.Errors[0].Code);
    }

    [Fact]
    public async Task Admin_Deactivate_SetsDeactivatedUtc_AndReactivateClearsIt()
    {
        User admin = Actor(Role.Admin);
        User target = Actor(Role.Peddler);
        await _store.AddAsync(admin);
        await _store.AddAsync(target);

        Result<User> deactivated = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: false, admin.Id, T0));
        Assert.True(deactivated.IsSuccess);
        Assert.Equal(T0, deactivated.Value.DeactivatedUtc);

        Result<User> reactivated = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: true, admin.Id, T0.AddHours(1)));
        Assert.True(reactivated.IsSuccess);
        Assert.Null(reactivated.Value.DeactivatedUtc);
    }

    [Fact]
    public async Task NonAdmin_Deactivate_RejectedForbidden()
    {
        User supervisor = Actor(Role.Supervisor);
        User target = Actor(Role.Peddler);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(target);

        Result<User> result = await _service.SetUserActiveAsync(
            new SetUserActiveCommand(target.Id, Active: false, supervisor.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("FORBIDDEN", result.Errors[0].Code);
    }

    [Fact]
    public async Task Admin_ChangePassword_ForAnotherUser_Succeeds()
    {
        User admin = Actor(Role.Admin);
        User target = Actor(Role.Peddler);
        await _store.AddAsync(admin);
        await _store.AddAsync(target);

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

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(supervisor.Id, "own-secret", supervisor.Id, T0));

        Assert.True(result.IsSuccess);
        Assert.Equal("hash:own-secret", result.Value.PasswordHash);
    }

    [Fact]
    public async Task Supervisor_ChangeAnothersPassword_RejectedForbidden()
    {
        User supervisor = Actor(Role.Supervisor);
        User target = Actor(Role.Peddler);
        await _store.AddAsync(supervisor);
        await _store.AddAsync(target);

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

        Result<User> result = await _service.ChangePasswordAsync(
            new ChangePasswordCommand(new UserId(Guid.NewGuid()), "pw", admin.Id, T0));

        Assert.True(result.IsFailure);
        Assert.Equal("USER_UNKNOWN", result.Errors[0].Code);
    }
}