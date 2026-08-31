using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U5 — CloseLockTests (docs/05 lane 9, close half): close a day through one
/// store; a SECOND store over the same file must reject a post-close sale
/// with DAY_NOT_OPEN; after an authorized reopen the sale passes.
/// </summary>
public sealed class CloseLockTests : PersistenceTestBase
{
    [Fact]
    public async Task ClosedDay_BlocksSecondStore_UntilReopen()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var (product, user) = Known();

        // Store 1 (the host singleton): seed the day and close it.
        var store1 = host.GetRequiredService<SqliteLedgerStore>();
        SeedDay(store1, product, user, opening: 100);
        var closeService = host.GetRequiredService<ICloseService>();
        Result<CloseOutcome> close = closeService.Close(new DayCloseCommand(
            new CloseId(Guid.NewGuid()), SeedDayKey, CountedCashCentavos: 0, null, user, SeedWhen.AddHours(2)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code);
        Assert.Equal(DayLifecycleState.Closed, store1.State.Day(SeedDayKey));
        store1.Close();

        // Store 2: a fresh connection over the same file sees the closed day
        // (the host singleton reopens lazily and its hot fold is post-close).
        var store2 = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        var checkout = host.GetRequiredService<ICheckoutService>();
        Result<CheckoutOutcome> blocked = checkout.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            OneUnitSale(SeedDayKey, product, "R-CLOSED", user, qty: 1));
        Assert.True(blocked.IsFailure);
        Assert.Contains(blocked.Errors, e => e.Code == "DAY_NOT_OPEN");
        Assert.Equal(0, await SqliteTest.CountAsync(DbPath(root), "Sales"));

        // Authorized reopen, then the sale passes.
        Result<AppendReceipt> reopen = host.GetRequiredService<IReopenService>().Reopen(new DayReopenCommand(
            new DayId(Guid.NewGuid()), SeedDayKey, "weekly reconciliation", user, user, SeedWhen.AddHours(2)));
        Assert.True(reopen.IsSuccess, reopen.Errors.FirstOrDefault()?.Code);
        Assert.Equal(DayLifecycleState.Open, store1.State.Day(SeedDayKey));

        Result<CheckoutOutcome> after = checkout.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            OneUnitSale(SeedDayKey, product, "R-AFTER-REOPEN", user, qty: 1));
        Assert.True(after.IsSuccess, string.Join("; ", after.Errors.Select(e => e.Code)));
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }
}
