using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U2 — ConcurrencyTests (docs/05 lane 5): two store instances over ONE file,
/// barrier-synchronized, race the last unit (BG-003 §6 canonical
/// interleaving). Exactly one append wins; the loser re-folds inside its
/// transaction and gets STOCK_NEGATIVE; committed stock is 0 and the stream
/// never holds a negative stock quantity.
/// </summary>
public sealed class ConcurrencyTests : PersistenceTestBase
{
    [Fact]
    public async Task TwoStores_RaceTheLastUnit_OneOk_OneStockNegative_CommittedStockZero()
    {
        string root = NewRoot();
        Lane8Fixture.Seed(root); // one sellable unit at the route, fixed ids

        var paths = new DataPathProvider(root);
        var store1 = new SqliteLedgerStore(paths, new DebugOptions());
        var store2 = new SqliteLedgerStore(paths, new DebugOptions());

        store1.Bind();
        store2.Bind();

        (Sale doc1, IReadOnlyList<Movement> mv1) = Lane8Fixture.BuildSale(store1);
        (Sale doc2, IReadOnlyList<Movement> mv2) = Lane8Fixture.BuildSale(store2);

        var gate = new ManualResetEventSlim(false);
        Task<Result<AppendReceipt>> run1 = Task.Run(() =>
        {
            gate.Wait();
            store1.RegisterSale(doc1);
            store1.Bind();
            return store1.Append(mv1);
        });
        Task<Result<AppendReceipt>> run2 = Task.Run(() =>
        {
            gate.Wait();
            store2.RegisterSale(doc2);
            store2.Bind();
            return store2.Append(mv2);
        });

        gate.Set();
        Result<AppendReceipt> result1 = await run1;
        Result<AppendReceipt> result2 = await run2;

        Assert.True(result1.IsSuccess != result2.IsSuccess, "exactly one append must win");
        Result<AppendReceipt> winner = result1.IsSuccess ? result1 : result2;
        Result<AppendReceipt> loser = result1.IsSuccess ? result2 : result1;
        Assert.Contains(loser.Errors, e => e.Code == "STOCK_NEGATIVE");
        Assert.True(winner.Value.LastSequence > 0);

        // Committed stock is 0: a fresh store re-folds the persisted stream.
        var reopened = new SqliteLedgerStore(paths, new DebugOptions());
        Assert.Equal(Quantity.Zero, reopened.State.StockOnHand(Lane8Fixture.Sardines, Lane8Fixture.Route));

        // MIN(quantity) over the stream >= 1 (no negative stock row can commit).
        long? minQuantity = await SqliteTest.MinAsync(DbPath(root), "Movements", "quantity");
        Assert.NotNull(minQuantity);
        Assert.True(minQuantity >= 1, $"MIN(quantity) = {minQuantity}");

        // A rebuild replays cleanly and the projections match the fold.
        await reopened.RebuildAsync();
        Assert.Equal(Quantity.Zero, reopened.State.StockOnHand(Lane8Fixture.Sardines, Lane8Fixture.Route));
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(DbPath(root)));
    }
}
