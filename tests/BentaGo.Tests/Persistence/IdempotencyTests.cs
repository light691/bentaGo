using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U9 — IdempotencyTests (docs/05 lane 4, cross-restart): through
/// ICheckoutService, the same checkout key returns the SAME receipt reference
/// with ONE sale — same instance, across a fresh store (restart), and under
/// concurrent submits.
/// </summary>
public sealed class IdempotencyTests : PersistenceTestBase
{
    private static async Task<ServiceProvider> SeedHostAsync(string root)
    {
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        return host;
    }

    [Fact]
    public async Task SameKey_TwiceSameInstance_OneSale_SameReference()
    {
        string root = NewRoot();
        ServiceProvider host = await SeedHostAsync(root);
        var (product, user) = Known();

        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        SeedDay(store, product, user);

        var checkout = host.GetRequiredService<ICheckoutService>();
        var key = new CheckoutKey(Guid.NewGuid());
        Result<CheckoutOutcome> first = checkout.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9-1", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);

        Result<CheckoutOutcome> second = checkout.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9-1-IGNORED", user, qty: 9));
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.Reference, second.Value.Reference);

        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }

    [Fact]
    public async Task SameKey_AcrossRestart_OneSale_SameReference()
    {
        string root = NewRoot();
        ServiceProvider host = await SeedHostAsync(root);
        var (product, user) = Known();

        var paths = host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>();
        var store1 = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        SeedDay(store1, product, user);

        var checkout1 = host.GetRequiredService<ICheckoutService>();
        var key = new CheckoutKey(Guid.NewGuid());
        Result<CheckoutOutcome> first = checkout1.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9R-1", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);

        store1.Close();

        // "Restart": a fresh store over the same file answers the same key.
        var store2 = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        var checkout2 = host.GetRequiredService<ICheckoutService>();
        Result<CheckoutOutcome> second = checkout2.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9R-IGNORED", user, qty: 1));
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.Reference, second.Value.Reference);
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }

    [Fact]
    public async Task SameKey_Concurrent_OneSale_IdenticalReferences()
    {
        string root = NewRoot();
        ServiceProvider host = await SeedHostAsync(root);
        var (product, user) = Known();

        var paths = host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>();
        var seedStore = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        SeedDay(seedStore, product, user);
        seedStore.Close();

        // Two independent store instances + two service instances, same key.
        var key = new CheckoutKey(Guid.NewGuid());
        var storeA = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        var storeB = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        var serviceA = new CheckoutService(storeA);
        var serviceB = new CheckoutService(storeB);

        var gate = new ManualResetEventSlim(false);
        Task<Result<CheckoutOutcome>> runA = Task.Run(() =>
        {
            gate.Wait();
            return serviceA.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9C-1", user, qty: 1));
        });
        Task<Result<CheckoutOutcome>> runB = Task.Run(() =>
        {
            gate.Wait();
            return serviceB.Checkout(key, OneUnitSale(SeedDayKey, product, "R-U9C-2", user, qty: 1));
        });

        gate.Set();
        Result<CheckoutOutcome> resultA = await runA;
        Result<CheckoutOutcome> resultB = await runB;

        Assert.True(resultA.IsSuccess, resultA.Errors.FirstOrDefault()?.Code);
        Assert.True(resultB.IsSuccess, resultB.Errors.FirstOrDefault()?.Code);
        Assert.Equal(resultA.Value.Reference, resultB.Value.Reference); // identical references every time
        Assert.True(resultA.Value.Replayed != resultB.Value.Replayed, "exactly one submit commits");
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }
}
