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
/// F8 session actor lifecycle (BG-005d) through the REAL login path: the
/// host's singleton IActorContext starts null (host-start cleared), a login
/// SUCCESS binds the signed-in user, and a failed attempt never touches the
/// context. The account use cases re-read the Users row on every call, so a
/// deactivated session actor is rejected (AUTH_ACTOR_DEACTIVATED) even
/// though deactivation does not clear the context (by design; the future
/// sign-out Clear is the BG-005d-ui logout gap).
/// </summary>
public sealed class SessionActorLifecycleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-actor-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider? _host;

    private async Task<ServiceProvider> CreateHostAsync()
    {
        var host = BentaGoHost.Build(development: true, dataRootOverride: _tempRoot);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();
        _host = host;
        return host;
    }

    [Fact]
    public async Task FreshHost_SessionActor_IsCleared()
    {
        ServiceProvider host = await CreateHostAsync();

        // A fresh host build constructs the context with no actor: host start
        // IS the cleared state (no explicit clear call needed).
        Assert.Null(host.GetRequiredService<IActorContext>().CurrentUserId);
    }

    [Fact]
    public async Task LoginSuccess_SetsTheSessionActor()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var contexts = host.GetRequiredService<IActorContext>();

        AuthResult result = await auth.AuthenticateAsync("admin", "admin");

        Assert.True(result.Success);
        Assert.Equal(result.UserId, contexts.CurrentUserId);
    }

    [Fact]
    public async Task LoginFailure_LeavesTheSessionActorUnchanged()
    {
        ServiceProvider host = await CreateHostAsync();
        var auth = host.GetRequiredService<IAuthenticationService>();
        var contexts = host.GetRequiredService<IActorContext>();

        // Failure with no prior session: still no actor (never a partial set).
        await auth.AuthenticateAsync("admin", "wrong-password");
        Assert.Null(contexts.CurrentUserId);

        // Success sets the actor; a later failed attempt does NOT clear it
        // (a failed re-login is not a sign-out).
        await auth.AuthenticateAsync("admin", "admin");
        UserId? signedIn = contexts.CurrentUserId;
        Assert.NotNull(signedIn);

        await auth.AuthenticateAsync("admin", "wrong-password");
        Assert.Equal(signedIn, contexts.CurrentUserId);
    }

    [Fact]
    public async Task DeactivatedSessionActor_IsRejectedByUseCase_DespiteTheContext()
    {
        ServiceProvider host = await CreateHostAsync();
        var accounts = host.GetRequiredService<IUserAccountStore>();
        var users = host.GetRequiredService<IUserAccountService>();
        var contexts = host.GetRequiredService<IActorContext>();
        var admin = await accounts.FindByUsernameAsync("admin");
        Assert.NotNull(admin);
        contexts.Set(admin.Id);

        // Two colleague accounts: one peddler to act on, one admin to later
        // deactivate and impersonate (the session's own admin cannot be
        // deactivated — self-guard F6).
        var peddler = await users.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "victim", "pw", Role.Peddler, PeddlerLink: null, admin.Id, T0));
        Assert.True(peddler.IsSuccess);
        var secondAdmin = await users.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "second-admin", "pw2", Role.Admin, PeddlerLink: null, admin.Id, T0));
        Assert.True(secondAdmin.IsSuccess);

        var deact = await users.SetUserActiveAsync(new SetUserActiveCommand(
            secondAdmin.Value.Id, Active: false, admin.Id, T0));
        Assert.True(deact.IsSuccess);

        // Sign the deactivated admin in and act as them: the context still
        // holds them (deactivation does not clear the session), but the fresh
        // Users-row read rejects the capability gate.
        contexts.Set(secondAdmin.Value.Id);
        var rejected = await users.CreateUserAsync(new CreateUserCommand(
            new UserId(Guid.NewGuid()), "ghost", "pw3", Role.Peddler, PeddlerLink: null, secondAdmin.Value.Id, T0));

        Assert.True(rejected.IsFailure);
        Assert.Equal("AUTH_ACTOR_DEACTIVATED", rejected.Errors[0].Code);
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
