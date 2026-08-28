using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U5 — CloseLockTests (docs/05 lane 9, close half): close a trip through one
/// store; a SECOND store over the same file must reject a post-close sale
/// with TRIP_CLOSED; after an authorized reopen the sale passes.
/// </summary>
public sealed class CloseLockTests : PersistenceTestBase
{
    [Fact]
    public async Task ClosedTrip_BlocksSecondStore_UntilReopen()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (trip, product, route, storeLoc, user, peddler) = Known();

        // Store 1 (the host singleton): seed the day and close the trip.
        var store1 = host.GetRequiredService<SqliteLedgerStore>();
        SeedDay(store1, trip, product, route, storeLoc, user, peddler, opening: 100, loaded: 10);
        var closeService = host.GetRequiredService<ICloseService>();
        Result<CloseOutcome> close = closeService.Close(new CloseCommand(
            new CloseId(Guid.NewGuid()), trip, Money.FromPesos(225m), null, user, SeedWhen.AddHours(2)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code);
        Assert.Equal(TripStatus.Closed, store1.State.Trip(trip).Status);
        store1.Close();

        // Store 2: a fresh connection over the same file sees the closed trip
        // (the host singleton reopens lazily and its hot fold is post-close).
        var store2 = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        var checkout = host.GetRequiredService<ICheckoutService>();
        Result<CheckoutOutcome> blocked = checkout.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            OneUnitSale(trip, product, "R-CLOSED", user, qty: 1));
        Assert.True(blocked.IsFailure);
        Assert.Contains(blocked.Errors, e => e.Code == "TRIP_CLOSED");
        Assert.Equal(0, await SqliteTest.CountAsync(DbPath(root), "Sales"));

        // Authorized reopen, then the sale passes.
        Result<AppendReceipt> reopen = host.GetRequiredService<IReopenService>().Reopen(new ReopenCommand(
            trip, "weekly reconciliation", user, SeedWhen.AddHours(2)));
        Assert.True(reopen.IsSuccess, reopen.Errors.FirstOrDefault()?.Code);
        Assert.Equal(TripStatus.Open, store1.State.Trip(trip).Status);

        Result<CheckoutOutcome> after = checkout.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            OneUnitSale(trip, product, "R-AFTER-REOPEN", user, qty: 1));
        Assert.True(after.IsSuccess, string.Join("; ", after.Errors.Select(e => e.Code)));
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }
}
