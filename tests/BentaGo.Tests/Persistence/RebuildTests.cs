using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U4 — RebuildTests (docs/05 lane 6): after a persisted two-peddler day,
/// RebuildAsync replays the stream through the real fold; the rebuilt state,
/// every balance surface, and the render equal the pre-rebuild values exactly
/// and LedgerHead lands on the stream max.
/// </summary>
public sealed class RebuildTests : PersistenceTestBase
{
    [Fact]
    public async Task PersistedDay_Rebuild_StateProjectionsAndRenderIdentical()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        string dbPath = DbPath(root);

        var store = host.GetRequiredService<SqliteLedgerStore>();
        TwoPeddlersScenario.Run(store);

        string before = TwoPeddlersScenario.Render(store).Text;
        long movements = store.Movements.Count;
        long last = store.State.LastSequence;
        var funds = Enum.GetValues<Fund>().ToDictionary(f => f, f => store.State.FundsOnHand(f));
        var cells = new List<(ProductId, LocationId)>();
        foreach (Movement m in store.Movements)
        {
            switch (m)
            {
                case OpeningStock s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case TransferOut t:
                    cells.Add((t.ProductId, t.LocationId));
                    break;
                case TransferIn t:
                    cells.Add((t.ProductId, t.LocationId));
                    break;
                case SaleIssue s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case SaleIssueRevoke s:
                    cells.Add((s.ProductId, s.LocationId));
                    break;
                case ReturnToStock r:
                    cells.Add((r.ProductId, r.LocationId));
                    break;
                case Damage d:
                    cells.Add((d.ProductId, d.LocationId));
                    break;
                case Loss l:
                    cells.Add((l.ProductId, l.LocationId));
                    break;
                case AdjustmentIncrease a:
                    cells.Add((a.ProductId, a.LocationId));
                    break;
                case AdjustmentDecrease a:
                    cells.Add((a.ProductId, a.LocationId));
                    break;
                case CountObserved c:
                    cells.Add((c.ProductId, c.LocationId));
                    break;
            }
        }

        var stockBefore = cells.Distinct().ToDictionary(c => c, c => store.State.StockOnHand(c.Item1, c.Item2));
        var tripsBefore = store.Context.Trips.ToDictionary(t => t.Id, t => store.State.Trip(t.Id));

        RebuildReceipt receipt = await store.RebuildAsync();

        Assert.Equal(movements, receipt.MovementCount);
        Assert.Equal(last, receipt.LastSequence);
        Assert.Equal(last, await SqliteTest.HeadSequenceAsync(dbPath));
        Assert.Equal(last, store.State.LastSequence);

        Assert.Equal(before, TwoPeddlersScenario.Render(store).Text);
        foreach (Fund fund in Enum.GetValues<Fund>())
        {
            Assert.Equal(funds[fund], store.State.FundsOnHand(fund));
        }

        foreach ((ProductId product, LocationId location) in stockBefore.Keys)
        {
            Assert.Equal(stockBefore[(product, location)], store.State.StockOnHand(product, location));
        }

        foreach ((TripId trip, TripState state) in tripsBefore)
        {
            Assert.Equal(state, store.State.Trip(trip));
        }

        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(dbPath));

        // A reopened store folds to the same watermark (durable projections
        // and hot state agree after the rebuild).
        var reopened = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        Assert.Equal(store.State.LastSequence, reopened.State.LastSequence);
        reopened.Close();
    }
}
