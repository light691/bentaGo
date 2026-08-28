using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U1 — RollbackTests (docs/05 lane 3): a batch whose last movement fails the
/// fold leaves zero rows in every table, no projection change, and no
/// sequence gap; the forced mid-transaction seam likewise commits nothing
/// partial.
/// </summary>
public sealed class RollbackTests : PersistenceTestBase
{
    [Fact]
    public async Task BatchWhoseLastMovementFailsTheFold_ZeroRowsEverywhere()
    {
        string root = NewRoot();
        SqliteLedgerStore store = await NewMigratedStoreAsync(root);
        string dbPath = DbPath(root);

        var (trip, product, route, storeLoc, user, _) = Known();
        SeedDay(store, trip, product, route, storeLoc, user, Known().Peddler, opening: 100, loaded: 10);

        long sales = await SqliteTest.CountAsync(dbPath, "Sales");
        long lines = await SqliteTest.CountAsync(dbPath, "SaleLines");
        long payments = await SqliteTest.CountAsync(dbPath, "PaymentLines");
        long movements = await SqliteTest.CountAsync(dbPath, "Movements");
        long audits = await SqliteTest.CountAsync(dbPath, "AuditEvents");
        long cells = await SqliteTest.CountAsync(dbPath, "CellBalances");
        long breakdowns = await SqliteTest.CountAsync(dbPath, "CellBreakdown");
        long seq = store.State.LastSequence;
        long head = await SqliteTest.HeadSequenceAsync(dbPath);

        // Two lines of the same product: each passes the builder's per-line
        // check (10 units on the route), but the batch sum overdraws.
        var lineA = new SaleLineId(Guid.NewGuid());
        var lineB = new SaleLineId(Guid.NewGuid());
        var payment = new PaymentLineId(Guid.NewGuid());
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            new SaleId(Guid.NewGuid()), trip, "R-OVER", user, SeedWhen.AddHours(2),
            new[]
            {
                new NewSaleLine(lineA, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(8), Money.Zero),
                new NewSaleLine(lineB, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(5), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(payment, PaymentMethod.Cash, "", Money.FromPesos(585m), Money.FromPesos(585m), Money.Zero),
            }));
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        store.RegisterSale(draft.Value.Document);
        store.Bind();

        Result<AppendReceipt> append = store.Append(draft.Value.Movements);
        Assert.True(append.IsFailure);
        Assert.Contains(append.Errors, e => e.Code == "STOCK_NEGATIVE");

        Assert.Equal(sales, await SqliteTest.CountAsync(dbPath, "Sales"));
        Assert.Equal(lines, await SqliteTest.CountAsync(dbPath, "SaleLines"));
        Assert.Equal(payments, await SqliteTest.CountAsync(dbPath, "PaymentLines"));
        Assert.Equal(movements, await SqliteTest.CountAsync(dbPath, "Movements"));
        Assert.Equal(audits, await SqliteTest.CountAsync(dbPath, "AuditEvents"));
        Assert.Equal(cells, await SqliteTest.CountAsync(dbPath, "CellBalances"));
        Assert.Equal(breakdowns, await SqliteTest.CountAsync(dbPath, "CellBreakdown"));
        Assert.Equal(seq, store.State.LastSequence);   // no sequence gap
        Assert.Equal(head, await SqliteTest.HeadSequenceAsync(dbPath));
    }

    [Fact]
    public async Task SeamForcedMidTransactionFailure_LeavesNothingPartial()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        string dbPath = DbPath(root);

        var paths = host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>();
        var debug = host.GetRequiredService<DebugOptions>();
        var store = new SqliteLedgerStore(paths, debug);
        var (trip, product, route, storeLoc, user, _) = Known();
        SeedDay(store, trip, product, route, storeLoc, user, Known().Peddler, opening: 100, loaded: 10);

        long sales = await SqliteTest.CountAsync(dbPath, "Sales");
        long movements = await SqliteTest.CountAsync(dbPath, "Movements");
        long audits = await SqliteTest.CountAsync(dbPath, "AuditEvents");
        long keys = await SqliteTest.CountAsync(dbPath, "IdempotencyKeys");
        long seq = store.State.LastSequence;

        debug.FailNextAppendAfterInsert();
        NewSaleCommand command = OneUnitSale(trip, product, "R-VALID", user, qty: 5);
        var service = new CheckoutService(store);
        Result<CheckoutOutcome> outcome = service.Checkout(new CheckoutKey(Guid.NewGuid()), command);

        Assert.True(outcome.IsFailure);
        Assert.Contains(outcome.Errors, e => e.Code == "FORCED_ROLLBACK");

        Assert.Equal(sales, await SqliteTest.CountAsync(dbPath, "Sales"));
        Assert.Equal(movements, await SqliteTest.CountAsync(dbPath, "Movements"));
        Assert.Equal(audits, await SqliteTest.CountAsync(dbPath, "AuditEvents"));
        Assert.Equal(keys, await SqliteTest.CountAsync(dbPath, "IdempotencyKeys"));
        Assert.Equal(seq, store.State.LastSequence);

        // The store still works afterwards (seam is latched).
        Result<CheckoutOutcome> retry = service.Checkout(new CheckoutKey(Guid.NewGuid()), OneUnitSale(trip, product, "R-VALID-2", user, qty: 5));
        Assert.True(retry.IsSuccess, retry.Errors.FirstOrDefault()?.Code);
    }
}
