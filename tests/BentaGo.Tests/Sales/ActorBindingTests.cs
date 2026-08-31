using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

public class ActorBindingTests : SalesTestBase
{
    private static User User(UserId id, bool deactivated = false) => new()
    {
        Id = id,
        Username = "u",
        PasswordHash = "h",
        Role = Role.Supervisor,
        DeactivatedUtc = deactivated ? SeedWhen : null,
        CreatedBy = id,
        CreatedUtc = SeedWhen,
    };

    [Fact]
    public async Task Resolve_No_Session_Is_AUTH_ACTOR_NOT_SIGNED_IN()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var actors = provider.GetRequiredService<IActorContext>();
        var users = provider.GetRequiredService<IUserAccountStore>();
        provider.Dispose();

        Result<User> r = await ActorGuard.ResolveAsync(actors, users);
        Assert.True(r.IsFailure);
        Assert.Equal("AUTH_ACTOR_NOT_SIGNED_IN", r.Errors[0].Code);
    }

    [Fact]
    public async Task Resolve_Unknown_User_Is_AUTH_ACTOR_UNKNOWN()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var actors = provider.GetRequiredService<IActorContext>();
        var users = provider.GetRequiredService<IUserAccountStore>();
        actors.Set(new UserId(Guid.NewGuid()));
        provider.Dispose();

        Result<User> r = await ActorGuard.ResolveAsync(actors, users);
        Assert.True(r.IsFailure);
        Assert.Equal("AUTH_ACTOR_UNKNOWN", r.Errors[0].Code);
    }

    [Fact]
    public async Task Resolve_Deactivated_Is_AUTH_ACTOR_DEACTIVATED()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var actors = provider.GetRequiredService<IActorContext>();
        var users = provider.GetRequiredService<IUserAccountStore>();
        var id = new UserId(Guid.NewGuid());
        await users.AddAsync(User(id, deactivated: true));
        actors.Set(id);
        provider.Dispose();

        Result<User> r = await ActorGuard.ResolveAsync(actors, users);
        Assert.True(r.IsFailure);
        Assert.Equal("AUTH_ACTOR_DEACTIVATED", r.Errors[0].Code);
    }

    [Fact]
    public async Task Resolve_Happy_Path_Returns_User()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var actors = provider.GetRequiredService<IActorContext>();
        var users = provider.GetRequiredService<IUserAccountStore>();
        var id = new UserId(Guid.NewGuid());
        await users.AddAsync(User(id));
        actors.Set(id);
        provider.Dispose();

        Result<User> r = await ActorGuard.ResolveAsync(actors, users);
        Assert.True(r.IsSuccess);
        Assert.Equal(id, r.Value.Id);
    }

    [Fact]
    public async Task Verify_Mismatch_Is_AUTH_ACTOR_MISMATCH()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var actors = provider.GetRequiredService<IActorContext>();
        var sessionUser = new UserId(Guid.NewGuid());
        actors.Set(new UserId(Guid.NewGuid()));
        provider.Dispose();

        Result<bool> r = ActorGuard.Verify(actors, sessionUser);
        Assert.True(r.IsFailure);
        Assert.Equal("AUTH_ACTOR_MISMATCH", r.Errors[0].Code);
    }

    [Fact]
    public void Verify_Happy_Path_When_Context_Matches()
    {
        var actors = new ActorContext();
        var user = new UserId(Guid.NewGuid());
        actors.Set(user);
        Result<bool> r = ActorGuard.Verify(actors, user);
        Assert.True(r.IsSuccess);
    }
}
