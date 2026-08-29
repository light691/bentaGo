using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Foundation;

/// <summary>
/// BG-005a authentication against the real Users table: seeded admin login,
/// generic audit messages that never reveal the username, lockout policy
/// integration (5 fails -> locked for 15 minutes; success resets; expiry
/// unlocks and resets the counter), and the unchanged ILoginService
/// compatibility path. An injected clock drives the lock expiry.
/// </summary>
public sealed class AuthenticationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-auth-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _clock = T0;
    private ServiceProvider? _host;

    private async Task<ServiceProvider> CreateHostAsync()
    {
        var host = BentaGoHost.Build(
            development: true,
            dataRootOverride: _tempRoot,
            extra: services => services.AddSingleton<Func<DateTimeOffset>>(() => _clock));
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();
        _host = host;
        return host;
    }

    [Fact]
    public async Task SeededAdmin_Authenticates_WithRoleAndUserId()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();

        AuthResult result = await auth.AuthenticateAsync("admin", "admin");

        Assert.True(result.Success);
        Assert.Equal(Role.Admin, result.Role);
        Assert.NotNull(result.UserId);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task WrongPassword_KnownUsername_ReturnsInvalidCredentials_AndRecordsAnonymousSafeAudit()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var store = host.GetRequiredService<SqliteLedgerStore>();
        var contextFactory = host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var admin = await context.Users.SingleAsync(u => u.Username == "admin");

        AuthResult result = await auth.AuthenticateAsync("admin", "wrong-password");

        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.FailureReason);
        Assert.Null(result.UserId);

        var audit = Assert.Single(store.Audits);
        Assert.Equal(AuthenticationAudit.LoginFailureCategory, audit.Category);
        Assert.Equal(AuthenticationAudit.InvalidCredentialsMessage, audit.Message);
        Assert.DoesNotContain("admin", audit.Message, StringComparison.Ordinal); // never reveals the username
        Assert.Equal(admin.Id, audit.Actor); // attributed to the known account
        Assert.NotNull(audit.Terminal);      // docs/01:77 actor/time/terminal/reason
        Assert.Null(audit.Reason);
        Assert.True(audit.Sequence > 0);     // persisted identity, not the -1 placeholder
    }

    [Fact]
    public async Task WrongPassword_UnknownUsername_RecordsAnonymousActorAudit()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        AuthResult result = await auth.AuthenticateAsync("no.such.user", "wrong-password");

        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.FailureReason);

        var audit = Assert.Single(store.Audits);
        Assert.Equal(UserId.Anonymous, audit.Actor); // cannot attribute -> sentinel
        Assert.Equal(AuthenticationAudit.InvalidCredentialsMessage, audit.Message);
        Assert.DoesNotContain("no.such.user", audit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_RecordsLoginSuccessAudit()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        await auth.AuthenticateAsync("admin", "admin");

        var audit = Assert.Single(store.Audits);
        Assert.Equal(AuthenticationAudit.LoginSuccessCategory, audit.Category);
        Assert.Equal(AuthenticationAudit.LoginSuccessMessage, audit.Message);
        Assert.NotEqual(UserId.Anonymous, audit.Actor);
        Assert.True(audit.Sequence > 0);
    }

    [Fact]
    public async Task FiveFailures_LockTheAccount_SixthAttempt_ReturnsAccountLocked_ExpiryUnlocks()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        for (int i = 0; i < AccountLockout.MaxFailedAttempts; i++)
        {
            AuthResult failed = await auth.AuthenticateAsync("admin", "wrong-password");
            Assert.Equal(AuthFailureReason.InvalidCredentials, failed.FailureReason);
        }

        // The 6th attempt — even with the correct password — is refused while locked.
        AuthResult locked = await auth.AuthenticateAsync("admin", "admin");
        Assert.False(locked.Success);
        Assert.Equal(AuthFailureReason.AccountLocked, locked.FailureReason);

        Assert.Contains(store.Audits, a =>
            a.Category == AuthenticationAudit.LoginFailureCategory &&
            a.Message == AuthenticationAudit.AccountLockedMessage);

        // Expiry unlocks; the counter reset lets the correct password through.
        _clock = T0.AddMinutes(16);
        AuthResult afterExpiry = await auth.AuthenticateAsync("admin", "admin");
        Assert.True(afterExpiry.Success);
        Assert.Equal(Role.Admin, afterExpiry.Role);

        var contextFactory = host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var admin = await context.Users.SingleAsync(u => u.Username == "admin");
        Assert.Equal(0, admin.FailedAttempts);       // success cleared the counter
        Assert.Null(admin.LockedUntilUtc);           // and the lock
    }

    [Fact]
    public async Task Success_BetweenFailures_ResetsTheCounter()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();

        for (int i = 0; i < 2; i++)
        {
            await auth.AuthenticateAsync("admin", "wrong-password");
        }

        Assert.True((await auth.AuthenticateAsync("admin", "admin")).Success);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                AuthFailureReason.InvalidCredentials,
                (await auth.AuthenticateAsync("admin", "wrong-password")).FailureReason);
        }

        // 2 + 3 failures with a success in between => 3, never a lock.
        var contextFactory = host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var admin = await context.Users.SingleAsync(u => u.Username == "admin");
        Assert.Equal(3, admin.FailedAttempts);
        Assert.Null(admin.LockedUntilUtc);
        Assert.True((await auth.AuthenticateAsync("admin", "admin")).Success);
    }

    [Fact]
    public async Task DeactivatedUser_CannotSignIn_GenericFailure()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var accounts = host.GetRequiredService<IUserAccountStore>();
        var users = host.GetRequiredService<IUserAccountService>();
        var admin = await accounts.FindByUsernameAsync("admin");

        // The account use cases act as the SESSION actor (F8, BG-005d): sign
        // the seeded admin into the context first — the same seam the login
        // path sets on success.
        var contexts = host.GetRequiredService<IActorContext>();
        contexts.Set(admin!.Id);

        // A deactivated account must exist to try: create a peddler, then
        // deactivate them (the admin cannot deactivate their own account, F6).
        var created = await users.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "peddie", "pw", Role.Peddler, PeddlerLink: null, admin.Id, T0));
        Assert.True(created.IsSuccess);

        var deact = await users.SetUserActiveAsync(new SetUserActiveCommand(
            created.Value.Id, Active: false, admin.Id, T0));
        Assert.True(deact.IsSuccess);

        AuthResult result = await auth.AuthenticateAsync("peddie", "pw");

        // Generic InvalidCredentials — never a distinct "deactivated" reason,
        // so the failure text cannot reveal account state.
        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.FailureReason);

        var audit = host.GetRequiredService<SqliteLedgerStore>().Audits.Last();
        Assert.Equal(AuthenticationAudit.LoginFailureCategory, audit.Category);
        Assert.Equal(AuthenticationAudit.InvalidCredentialsMessage, audit.Message);
    }

    [Fact]
    public async Task EmptyOrNullPassword_IsNotAnAttempt_NoAudit_NoCounterIncrement()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        AuthResult empty = await auth.AuthenticateAsync("admin", "");
        AuthResult nul = await auth.AuthenticateAsync("admin", null!);
        AuthResult blankUsername = await auth.AuthenticateAsync("", "admin");

        Assert.Equal(AuthFailureReason.InvalidCredentials, empty.FailureReason);
        Assert.Equal(AuthFailureReason.InvalidCredentials, nul.FailureReason);
        Assert.Equal(AuthFailureReason.InvalidCredentials, blankUsername.FailureReason);
        Assert.Empty(store.Audits); // blank submits are NOT attempts: no audit rows

        // And the counter is untouched: exactly 4 real wrong-password failures
        // must still leave the account unlocked (4 < 5).
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(AuthFailureReason.InvalidCredentials,
                (await auth.AuthenticateAsync("admin", "wrong-" + i)).FailureReason);
        }

        AuthResult good = await auth.AuthenticateAsync("admin", "admin");
        Assert.True(good.Success, "blank submits must not count toward lockout");
    }

    [Fact]
    public async Task ILoginService_CompatibilityPath_StillAuthenticates()
    {
        ServiceProvider host = await CreateHostAsync();
        var legacy = host.GetRequiredService<ILoginService>();

        Assert.True(await legacy.AuthenticateAsync("admin", "admin"));
        Assert.False(await legacy.AuthenticateAsync("admin", "wrong-password"));
        Assert.False(await legacy.AuthenticateAsync("", "admin"));
    }

    // Session-actor lifecycle coverage (login sets / failure unchanged /
    // fresh-host cleared) lives in SessionActorLifecycleTests — it exercises
    // the LoginService wiring that lands with the BG-005d Infrastructure slice.

    public void Dispose()
    {
        _host?.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the throwaway root.
        }
    }
}
