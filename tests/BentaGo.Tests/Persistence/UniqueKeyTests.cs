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
/// DUP_RECEIPT_NUMBER.
/// </summary>
public sealed class UniqueKeyTests : PersistenceTestBase
{
    [Fact]
    public async Task SameCheckoutKeyTwice_OneSale_SameReceiptReference()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (product, user) = Known();
        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        SeedDay(store, product, user, opening: 100);

        var checkout = host.GetRequiredService<ICheckoutService>();
        var key = new CheckoutKey(Guid.NewGuid());
        Result<CheckoutOutcome> first = checkout.Checkout(key, OneUnitSale(SeedDayKey, product, "R-SAME", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);
        Assert.False(first.Value.Replayed);

        Result<CheckoutOutcome> second = checkout.Checkout(key, OneUnitSale(SeedDayKey, product, "R-SAME-OTHER", user, qty: 2));
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

        var (product, user) = Known();
        var store = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        SeedDay(store, product, user, opening: 100);

        var checkout = host.GetRequiredService<ICheckoutService>();
        Result<CheckoutOutcome> first = checkout.Checkout(new CheckoutKey(Guid.NewGuid()), OneUnitSale(SeedDayKey, product, "R-DUP", user, qty: 1));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);

        Result<CheckoutOutcome> second = checkout.Checkout(new CheckoutKey(Guid.NewGuid()), OneUnitSale(SeedDayKey, product, "R-DUP", user, qty: 1));
        Assert.True(second.IsFailure);
        Assert.Contains(second.Errors, e => e.Code == "DUP_RECEIPT_NUMBER");
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales")); // the failed one rolled back
    }


}
