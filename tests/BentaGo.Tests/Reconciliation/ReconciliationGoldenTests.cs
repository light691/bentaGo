using System.Globalization;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Reconciliation;

/// <summary>
/// BG-008 Slice A golden contract (PersistedGoldenTests style): the two-peddler
/// day's reconciliation report renders byte-identically from in-memory and
/// SQLite, after a reopen, and after a rebuild — the report is a pure
/// projection of the ledger read surface and never touches the database.
/// </summary>
public sealed class ReconciliationGoldenTests : PersistenceTestBase
{
    [Fact]
    public async Task ReconciliationReport_MatchesInMemoryGolden_ByteForByte()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var sqlite = host.GetRequiredService<SqliteLedgerStore>();
        _ = TwoPeddlersScenario.Run(sqlite);

        var memory = new InMemoryLedgerStore();
        _ = TwoPeddlersScenario.Run(memory);

        (Trip aliceTrip, PeddlerId alice) = AliceOf(sqlite);

        ReconciliationReport persisted = new ReconciliationReportService(sqlite).Build(alice, aliceTrip.Id).Value;
        ReconciliationReport inMemory = new ReconciliationReportService(memory).Build(alice, aliceTrip.Id).Value;

        string persistedText = Render(persisted);
        string inMemoryText = Render(inMemory);
        Assert.Equal(inMemoryText, persistedText);
        Assert.NotEmpty(persistedText);

        // A fresh store over the same file re-folds identically.
        var reopened = new SqliteLedgerStore(
            host.GetRequiredService<IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        ReconciliationReport reopenedReport = new ReconciliationReportService(reopened).Build(alice, aliceTrip.Id).Value;
        Assert.Equal(persistedText, Render(reopenedReport));

        // And after a rebuild the report is still byte-identical.
        await reopened.RebuildAsync();
        ReconciliationReport rebuiltReport = new ReconciliationReportService(reopened).Build(alice, aliceTrip.Id).Value;
        Assert.Equal(persistedText, Render(rebuiltReport));
    }

    [Fact]
    public async Task ReportTotals_EqualLedgerFold_AndScenarioRows()
    {
        var memory = new InMemoryLedgerStore();
        _ = TwoPeddlersScenario.Run(memory);

        (Trip aliceTrip, PeddlerId alice) = AliceOf(memory);

        ReconciliationReport report = new ReconciliationReportService(memory).Build(alice, aliceTrip.Id).Value;

        // Expected cash equals the trip's folded physical cash.
        Assert.Equal(memory.State.TripCash(aliceTrip.Id, Fund.PhysicalCash), report.Totals.ExpectedCash);
        Assert.Equal(2, report.Totals.Sales);          // R-000001 + R-000004
        Assert.Equal(Money.FromPesos(329m), report.Totals.SalesTotal); // 225 + 104
        Assert.Equal(1, report.Totals.Voided);         // R-000002
        Assert.Equal(1, report.Totals.Returns);        // return on sale 1
        Assert.Equal(Money.FromPesos(45m), report.Totals.RefundTotal);
        Assert.Equal(Money.FromPesos(284m), report.Totals.ExpectedCash);
        Assert.Equal(Money.FromPesos(180m), report.Totals.CountedCash);
        Assert.Equal(Money.FromPesos(104m), report.Totals.CashVariance);
        Assert.Equal(VarianceLabel.Shortage, report.Totals.CashLabel);

        // Per-product: sardines counted 38 vs expected 36 -> +2 overage @ 45.00.
        StockVarianceRow sardines = report.StockRows.Single(r => r.Sku == "Sardines");
        Assert.Equal(36, sardines.ExpectedUnits);
        Assert.Equal(38, sardines.CountedUnits);
        Assert.Equal(2, sardines.QuantityVariance);
        Assert.Equal(Money.FromPesos(90m), sardines.ValueVariance);
    }

    /// <summary>Identifies Alice's closed trip deterministically via its close document.</summary>
    private static (Trip Trip, PeddlerId Peddler) AliceOf(ILedgerStore store)
    {
        store.Bind();
        TripId tripId = store.Context.Closes[0].Trip; // only Alice's trip is closed
        Trip trip = store.Context.Trips.Single(t => t.Id == tripId);
        return (trip, trip.Peddler);
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
