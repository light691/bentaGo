using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Foundation;

/// <summary>
/// F2 — the timing-oracle guard. Paths that cannot run a real credential
/// check (unknown username, locked account, deactivated account) must still
/// burn one BCrypt verify against the fixed dummy hash so they cost the same
/// as a genuine wrong-password attempt. A recording hasher observes exactly
/// that the dummy hash is verified — deterministic, no timing measurements.
/// </summary>
public sealed class AuthTimingOracleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private sealed class RecordingHasher : IPasswordHasher
    {
        public List<(string Password, string Hash)> VerifyCalls { get; } = new();

        // "admin" is the only valid credential in the fake world.
        public string Hash(string password) => "fake-hash";

        public bool Verify(string password, string hash)
        {
            VerifyCalls.Add((password, hash));
            return hash == "fake-hash" && password == "admin";
        }
    }

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-timing-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider? _host;

    private async Task<ServiceProvider> CreateHostAsync(RecordingHasher hasher, bool seed)
    {
        var host = BentaGoHost.Build(
            development: true,
            dataRootOverride: _tempRoot,
            extra: services => services.AddSingleton<IPasswordHasher>(hasher));
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        if (seed)
        {
            await host.GetRequiredService<IAdminSeeder>().SeedAsync();
        }

        _host = host;
        return host;
    }

    [Fact]
    public async Task UnknownUsername_StillRuns_DummyHashVerify()
    {
        var hasher = new RecordingHasher();
        ServiceProvider host = await CreateHostAsync(hasher, seed: false); // no users -> every attempt is unknown
        var auth = host.GetRequiredService<IAuthenticationService>();

        AuthResult result = await auth.AuthenticateAsync("ghost.user", "whatever");

        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.FailureReason);
        Assert.Contains(hasher.VerifyCalls, c => c.Hash == AuthenticationAudit.DummyPasswordHash);
    }

    [Fact]
    public async Task LockedAccount_StillRuns_DummyHashVerify()
    {
        var hasher = new RecordingHasher();
        ServiceProvider host = await CreateHostAsync(hasher, seed: true);
        var auth = host.GetRequiredService<IAuthenticationService>();

        for (int i = 0; i < AccountLockout.MaxFailedAttempts; i++)
        {
            await auth.AuthenticateAsync("admin", "wrong");
        }

        hasher.VerifyCalls.Clear();
        AuthResult result = await auth.AuthenticateAsync("admin", "admin"); // correct password, but locked

        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.AccountLocked, result.FailureReason);
        Assert.Contains(hasher.VerifyCalls, c => c.Hash == AuthenticationAudit.DummyPasswordHash);
        Assert.DoesNotContain(hasher.VerifyCalls, c => c.Hash == "fake-hash"); // no real credential work while locked
    }

    [Fact]
    public async Task DeactivatedAccount_StillRuns_DummyHashVerify()
    {
        var hasher = new RecordingHasher();
        ServiceProvider host = await CreateHostAsync(hasher, seed: true);
        var auth = host.GetRequiredService<IAuthenticationService>();
        var accounts = host.GetRequiredService<IUserAccountStore>();
        var users = host.GetRequiredService<IUserAccountService>();
        var admin = await accounts.FindByUsernameAsync("admin");
        var created = await users.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "peddie", "pw", Role.Peddler, PeddlerLink: null, admin!.Id, T0));
        Assert.True(created.IsSuccess);
        var deact = await users.SetUserActiveAsync(new SetUserActiveCommand(
            created.Value.Id, Active: false, admin.Id, T0));
        Assert.True(deact.IsSuccess);

        hasher.VerifyCalls.Clear();
        AuthResult result = await auth.AuthenticateAsync("peddie", "pw");

        Assert.False(result.Success);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.FailureReason);
        Assert.Contains(hasher.VerifyCalls, c => c.Hash == AuthenticationAudit.DummyPasswordHash);
    }

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