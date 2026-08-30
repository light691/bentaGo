using System.Globalization;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Reconciliation;

/// <summary>
/// BG-008 Slice D two-peddler END-TO-END scenario (docs/05 line 456): run the
/// complete deterministic two-peddler day (loads, sales, split cash/GCash,
/// void, return, damage, physical count, close with variance, authorized
/// reopen, and the post-close-rejected audit), then render the reconciliation
/// report + both CSV exports. Assert EVERY difference is explained by the
/// report (expected vs counted cash, per-product stock variances, notes),
/// totals equal the ledger folds, and the report is byte-identical across
/// SQLite / in-memory / reopen / rebuild (the PersistedGoldenTests pattern).
/// </summary>
public sealed class TwoPeddlersEndToEndTests : PersistenceTestBase
{
    [Fact]
    public async Task TwoPeddlersFullDay_ReportExplainsEveryDifference_AndIsByteIdentical()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var sqlite = host.GetRequiredService<SqliteLedgerStore>();
        _ = TwoPeddlersScenario.Run(sqlite);

        var memory = new InMemoryLedgerStore();
        _ = TwoPeddlersScenario.Run(memory);

        (TripId trip, PeddlerId peddler) = AliceOf(sqlite);

        ReconciliationReport persisted = new ReconciliationReportService(sqlite).Build(peddler, trip).Value;
        ReconciliationReport inMemory = new ReconciliationReportService(memory).Build(peddler, trip).Value;

        string persistedText = Render(persisted);
        string inMemoryText = Render(inMemory);
        Assert.Equal(inMemoryText, persistedText);
        Assert.NotEmpty(persistedText);

        // Every difference on the day's data is EXPLAINED by the report and the
        // totals equal the ledger folds and the sale-history rows.
        AssertEveryDifferenceExplained(persisted, sqlite, peddler, trip);
        AssertTotalsEqualFoldAndHistory(persisted, sqlite, peddler, trip);

        // A fresh store over the same file re-folds and renders identically.
        var reopened = new SqliteLedgerStore(
            host.GetRequiredService<IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        Assert.Equal(persistedText, Render(new ReconciliationReportService(reopened).Build(peddler, trip).Value));

        // And after a rebuild the render is still byte-identical.
        await reopened.RebuildAsync();
        Assert.Equal(persistedText, Render(new ReconciliationReportService(reopened).Build(peddler, trip).Value));

        // Exports are a projection of the same facts and are byte-stable.
        var service = new ReconciliationReportService(sqlite);
        string reconCsv = service.BuildReconciliationCsv(peddler, trip).Value;
        string txnCsv = service.BuildTransactionCsv(peddler, trip).Value;
        Assert.StartsWith("Kind,Item,Expected,Counted,Variance,Value,Label,Notes\n", reconCsv);
        Assert.StartsWith("Date,Receipt,Lines,Methods,Amount,Status\n", txnCsv);
        Assert.Contains("Cash,Physical Cash,284.00,180.00,104.00,,SHORTAGE,", reconCsv);
        Assert.Contains("Stock,Sardines - Sardines,36,38,2,90.00,,", reconCsv);
        Assert.Contains("R-000001", txnCsv);
        Assert.Contains("R-000004", txnCsv);
    }

    private static void AssertEveryDifferenceExplained(
        ReconciliationReport report, ILedgerStore store, PeddlerId peddler, TripId trip)
    {
        // Cash: every difference has an expected, counted, signed variance and a label.
        CashVarianceRow cash = report.CashRows.Single();
        Assert.Equal(VarianceLabel.Shortage, cash.Label);
        Assert.Equal(store.State.TripCash(trip, Fund.PhysicalCash), cash.Expected);
        Assert.Equal(Money.FromPesos(180m), cash.Counted);
        Assert.Equal(Money.FromPesos(104m), cash.Variance);
        Assert.Equal(cash.Expected.Minus(cash.Counted), cash.Variance);

        // Stock: a counted product has a signed quantity + value variance and
        // explains its "counted - expected" difference; an uncounted product
        // reports a blank variance (nothing to explain).
        Assert.NotEmpty(report.StockRows);
        bool hasSardines = false;
        bool hasUncounted = false;
        foreach (StockVarianceRow row in report.StockRows)
        {
            Assert.Equal(row.CountedUnits is null, row.QuantityVariance is null);
            Assert.Equal(row.CountedUnits is null, row.ValueVariance is null);
            if (row.CountedUnits is { } counted)
            {
                Assert.Equal(counted - row.ExpectedUnits, row.QuantityVariance);
                Assert.Equal(row.UnitPrice.Times(row.QuantityVariance!.Value), row.ValueVariance);
                if (row.Sku == "Sardines")
                {
                    hasSardines = true;
                    Assert.Equal(36, row.ExpectedUnits);
                    Assert.Equal(38, counted);
                    Assert.Equal(2, row.QuantityVariance);
                    Assert.Equal(Money.FromPesos(90m), row.ValueVariance);
                }
            }
            else
            {
                hasUncounted = true;
            }
        }

        Assert.True(hasSardines, "the closed trip's Sardines overage must be explained");
        Assert.True(hasUncounted, "an uncounted line must appear with a blank variance");
    }

    private static void AssertTotalsEqualFoldAndHistory(
        ReconciliationReport report, ILedgerStore store, PeddlerId peddler, TripId trip)
    {
        ReportTotals totals = report.Totals;

        // Expected cash equals the trip's folded physical cash.
        Assert.Equal(store.State.TripCash(trip, Fund.PhysicalCash), totals.ExpectedCash);
        Assert.Equal(Money.FromPesos(180m), totals.CountedCash);
        Assert.Equal(Money.FromPesos(104m), totals.CashVariance);
        Assert.Equal(VarianceLabel.Shortage, totals.CashLabel);

        // Sales / voided / returns / refunds match the scenario and the query.
        Assert.Equal(2, totals.Sales);          // R-000001 + R-000004
        Assert.Equal(Money.FromPesos(329m), totals.SalesTotal); // 225 + 104
        Assert.Equal(1, totals.Voided);         // R-000002
        Assert.Equal(1, totals.Returns);
        Assert.Equal(Money.FromPesos(45m), totals.RefundTotal);

        var query = new SaleQueryService(store);
        IReadOnlyList<SaleHistoryRow> rows = query.Query(peddler).Value
            .Where(r => r.Trip == trip)
            .ToList();
        int committed = rows.Count(r => r.Status != SaleStatus.Voided);
        Money committedTotal = rows
            .Where(r => r.Status != SaleStatus.Voided)
            .Aggregate(Money.Zero, (acc, r) => acc.Plus(r.Total));
        Assert.Equal(committed, totals.Sales);
        Assert.Equal(committedTotal, totals.SalesTotal);
    }

    /// <summary>Identifies Alice's closed trip deterministically via its close document.</summary>
    private static (TripId TripId, PeddlerId Peddler) AliceOf(ILedgerStore store)
    {
        store.Bind();
        TripId tripId = store.Context.Closes.Single().Trip; // only Alice's trip is closed
        Trip trip = store.Context.Trips.Single(t => t.Id == tripId);
        return (tripId, trip.Peddler);
    }

    private static string Render(ReconciliationReport report)
    {
        var sb = new StringBuilder();
        foreach (CashVarianceRow cash in report.CashRows)
        {
            sb.AppendLine($"CASH {cash.Fund} expected={Fmt(cash.Expected)} counted={Fmt(cash.Counted)} variance={Fmt(cash.Variance)} label={cash.Label}");
        }

        foreach (StockVarianceRow stock in report.StockRows)
        {
            sb.AppendLine(
                $"STOCK {stock.Sku}|{stock.ProductName} expected={stock.ExpectedUnits} " +
                $"counted={stock.CountedUnits?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"qtyVar={stock.QuantityVariance?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"price={Fmt(stock.UnitPrice)} valueVar={(stock.ValueVariance is { } v ? Fmt(v) : "-")} " +
                $"notes={stock.Notes ?? "-"}");
        }

        ReportTotals totals = report.Totals;
        sb.AppendLine(
            $"TOTALS sales={totals.Sales} total={Fmt(totals.SalesTotal)} voided={totals.Voided} " +
            $"returns={totals.Returns} refunds={Fmt(totals.RefundTotal)} adjustments={totals.Adjustments} " +
            $"cashExpect={Fmt(totals.ExpectedCash)} cashCounted={Fmt(totals.CountedCash)} " +
            $"cashVar={Fmt(totals.CashVariance)} cashLabel={totals.CashLabel}");
        return sb.ToString();
    }

    private static string Fmt(Money money)
        => money.ToPesos().ToString("0.00", CultureInfo.InvariantCulture);
}
