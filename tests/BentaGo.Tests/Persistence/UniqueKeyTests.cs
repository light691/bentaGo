using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U3 — UniqueKeyTests (docs/05 lane 4 + docs/02): same checkout key twice ->
/// ONE sale and the SAME receipt reference; a duplicate receipt number ->
/// DUP_RECEIPT_NUMBER; a duplicate load number -> DUP_LOAD.
/// </summary>
public sealed class UniqueKeyTests : PersistenceTestBase
{
    [Fact]
    public async Task SameCheckoutKeyTwice_OneSale_SameReceiptReference()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (trip, product, route, storeLoc, user, _) = Known();
        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        SeedDay(store, trip, product, route, storeLoc, user, Known().Peddler, opening: 100, loaded: 10);

        var checkout = host.GetRequiredService<ICheckoutService>();
        var key = new CheckoutKey(Guid.NewGuid());
        Result<CheckoutOutcome> first = checkout.Checkout(key, OneUnitSale(trip, product, "R-SAME", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);
        Assert.False(first.Value.Replayed);

        Result<CheckoutOutcome> second = checkout.Checkout(key, OneUnitSale(trip, product, "R-SAME-OTHER", user, qty: 2));
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.Reference, second.Value.Reference); // same receipt reference

        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
        Result<CheckoutReference?> found = store.FindCheckout(key.Value.ToString("D"));
        Assert.True(found.IsSuccess);
        Assert.Equal(first.Value.Reference, found.Value);
    }

    [Fact]
    public async Task DuplicateReceiptNumber_FailsDupReceiptNumber()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (trip, product, route, storeLoc, user, _) = Known();
        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        SeedDay(store, trip, product, route, storeLoc, user, Known().Peddler, opening: 100, loaded: 10);

        var checkout = host.GetRequiredService<ICheckoutService>();
        Result<CheckoutOutcome> first = checkout.Checkout(new CheckoutKey(Guid.NewGuid()), OneUnitSale(trip, product, "R-DUP", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);

        Result<CheckoutOutcome> second = checkout.Checkout(new CheckoutKey(Guid.NewGuid()), OneUnitSale(trip, product, "R-DUP", user, qty: 1));
        Assert.True(second.IsFailure);
        Assert.Contains(second.Errors, e => e.Code == "DUP_RECEIPT_NUMBER");
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales")); // the failed one rolled back
    }

    [Fact]
    public async Task DuplicateLoadNumber_FailsDupLoad()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (trip, product, route, storeLoc, user, peddler) = Known();
        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        store.RegisterTrip(new Trip(trip, peddler, route, SeedWhen, user));
        store.Bind();

        var service = host.GetRequiredService<ILoadService>();
        Result<LoadOutcome> first = service.Load(new LoadCommand(
            new LoadId(Guid.NewGuid()), "LD-DUP", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(1), Money.FromPesos(0m)) },
            user, SeedWhen), peddler);
        Assert.True(first.IsFailure); // no stock at the store yet -> the append cannot transfer out
        Assert.Contains(first.Errors, e => e.Code == "STOCK_NEGATIVE");

        // Give the store stock so the load is legal, then collide the number.
        var storeLocLoad = new LoadId(Guid.NewGuid());
        store.RegisterLoad(new StockLoad(storeLocLoad, "LD-DUP-OPEN", trip, Array.Empty<StockLoadLine>(), user, SeedWhen));
        store.Bind();
        store.Append(new[] { Moves.OpeningStock(0, SeedWhen, user, new LoadRef(storeLocLoad), product, storeLoc, Quantity.Of(10)) });

        Result<LoadOutcome> ok = service.Load(new LoadCommand(
            new LoadId(Guid.NewGuid()), "LD-DUP", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(1), Money.FromPesos(0m)) },
            user, SeedWhen), peddler);
        Assert.True(ok.IsSuccess, ok.Errors.FirstOrDefault()?.Code);

        Result<LoadOutcome> collision = service.Load(new LoadCommand(
            new LoadId(Guid.NewGuid()), "LD-DUP", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(1), Money.FromPesos(0m)) },
            user, SeedWhen), peddler);
        Assert.True(collision.IsFailure);
        Assert.Contains(collision.Errors, e => e.Code == "DUP_LOAD");
        Assert.Equal(2, await SqliteTest.CountAsync(DbPath(root), "StockLoads"));
    }
}
