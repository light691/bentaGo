using System.Diagnostics;
using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.Tools;

/// <summary>
/// The BG-008 perf probe (docs/05 lines 486-489): measure "calculate and render
/// a close" against a seeded file with 10,000 committed sales and ~38,000
/// movements. Baselines FIRST (BG-003 ledger-aggregation median = the replay
/// fold; BG-006 history-load median = SaleQueryService.Query), then the two
/// metrics as P95 after ONE warm-up. Rules: close calculation P95 ≤ 1000 ms
/// and report render P95 ≤ 2000 ms.
///
/// The seed reuses the PerfRunner approach (register documents + Bind + a
/// single AppendBatch of movements). The reported day carries a realistic
/// small catalog (10 products) while a separate bulk block folds the remaining
/// movements into the ledger — the day report's product scan stays small
/// because it walks only the day's committed sales.
/// </summary>
public static class Bg008PerfRunner
{
    private const int SalesCount = 10_000;
    private const int PerfProducts = 10;        // the REPORTED day's catalog (realistic store day)
    private const int BulkProducts = 3_000;     // × 6 movements of non-sale ledger traffic
    private const int WarmUp = 3;
    private const int Samples = 30;

    private static readonly ScenarioIdSource Ids = new("perf-bg008-v1");

    private static readonly UserId User = new(Ids.Next("user"));

    private static readonly DateTimeOffset When = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly DayKey Day = DayKey.FromUtc(When);

    /// <summary>Product id derivable per index WITHOUT the shared counter.</summary>
    private static ProductId PerfProduct(int index) =>
        new(new ScenarioIdSource($"perf-bg008-product-{index}").Next("p"));

    /// <summary>
    /// Registers the trading day (movement-less day document) and rebinds, so
    /// builders and the close/reopen use cases see the day lifecycle. Idempotent.
    /// </summary>
    private static void EnsureDayOpen(ILedgerStore store)
    {
        store.RegisterDayOpen(new DayOpen(new DayId(Ids.Next("day")), Day, 0, User, When));
        store.Bind();
    }

    public static async Task<int> RunAsync(string root, string outPath, string head)
    {
        Console.WriteLine("PERF: BG-008 close + report probe (10,000 sales / bulk fold traffic)");

        var lines = new List<string>
        {
            $"# BG-008 perf probe — close calculation + report render, {SalesCount} sales, P95 after {WarmUp} warm-ups",
            $"head\t{head}",
        };

        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        string dbPath = Path.Combine(host.GetRequiredService<IDataPathProvider>().DataDir, "bentago.db");

        var seedWatch = Stopwatch.StartNew();
        (int sales, int movements) = await SeedAsync(dbPath);
        seedWatch.Stop();
        Console.WriteLine($"SEED: {sales} sales, {movements} movements in {seedWatch.ElapsedMilliseconds} ms");
        lines.Add($"seed\t{sales} sales / {movements} movements\t{seedWatch.ElapsedMilliseconds} ms");

        var store = host.GetRequiredService<SqliteLedgerStore>();
        EnsureDayOpen(store);

        // ---- Baselines FIRST (BG-003 ledger aggregation, BG-006 history load) ----
        double aggregationMedian = Median(Timed(() =>
            _ = LedgerState.Fold(store.Movements, store.Context.Sales,
                store.Context.Returns, store.Context.Voids), Samples));
        Console.WriteLine($"BASELINE_BG003_LEDGER_AGGREGATION_MEDIAN_MS: {aggregationMedian:F2}");
        lines.Add($"baseline\tbg-003 ledger aggregation median\t{aggregationMedian:F2} ms");

        double historyMedian = Median(Timed(() =>
            _ = new SaleQueryService(store).Query().Value, Samples));
        Console.WriteLine($"BASELINE_BG006_HISTORY_LOAD_MEDIAN_MS: {historyMedian:F2}");
        lines.Add($"baseline\tbg-006 history-load median\t{historyMedian:F2} ms");

        // ---- Metrics (close calculation P95, report render P95) after ONE warm-up ----
        var closeService = host.GetRequiredService<ICloseService>();
        var reopenService = host.GetRequiredService<IReopenService>();
        var reportService = host.GetRequiredService<IReconciliationReportService>();

        // Warm-up: close, reopen, report (excluded from the samples) so GC/JIT
        // and the WAL settle before the P95 is taken.
        for (int w = 0; w < WarmUp; w++)
        {
            Close(closeService, w);
            Reopen(reopenService, w + 1);
            _ = reportService.Build(Day).Value;
        }

        var closeSamples = new List<double>(Samples);
        var reportSamples = new List<double>(Samples);
        for (int i = 0; i < Samples; i++)
        {
            var cw = Stopwatch.StartNew();
            Close(closeService, i + 1);
            cw.Stop();
            closeSamples.Add(cw.Elapsed.TotalMilliseconds);

            var rw = Stopwatch.StartNew();
            Result<ReconciliationReport> report = reportService.Build(Day);
            rw.Stop();
            if (report.IsFailure)
            {
                throw new InvalidOperationException($"report failed: {report.Errors[0].Code}: {report.Errors[0].Message}");
            }

            reportSamples.Add(rw.Elapsed.TotalMilliseconds);
            Console.WriteLine($"CLOSE_{i + 1}_MS: {closeSamples[i]:F2}  REPORT_{i + 1}_MS: {reportSamples[i]:F2}");

            if (i < Samples - 1)
            {
                Reopen(reopenService, i + 2);
            }
        }

        closeSamples.Sort();
        reportSamples.Sort();
        double closeP95 = P95(closeSamples);
        double reportP95 = P95(reportSamples);
        Console.WriteLine($"METRIC_CLOSE_CALCULATION_P95_MS: {closeP95:F2} (rule <= 1000 ms)");
        Console.WriteLine($"METRIC_REPORT_RENDER_P95_MS: {reportP95:F2} (rule <= 2000 ms)");
        lines.Add($"metric\tbg-008 close calculation P95\t{closeP95:F2} ms\trule <= 1000 ms");
        lines.Add($"metric\tbg-008 report render P95\t{reportP95:F2} ms\trule <= 2000 ms");

        bool ok = closeP95 <= 1000 && reportP95 <= 2000;
        lines.Add($"verdict\tclose+report perf\t{(ok ? "PASS" : "FAIL")}");
        Console.WriteLine($"VERDICT: {(ok ? "PASS" : "FAIL")} (close <= 1000 ms: {closeP95 <= 1000}; report <= 2000 ms: {reportP95 <= 2000})");

        host.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllLinesAsync(outPath, lines);
        return ok ? 0 : 1;
    }

    private static async Task<(int Sales, int Movements)> SeedAsync(string dbPath)
    {
        var paths = new DataPathProvider(Path.GetDirectoryName(Path.GetDirectoryName(dbPath))!);
        var store = new SqliteLedgerStore(paths, new DebugOptions());

        DateTimeOffset t0 = When;
        EnsureDayOpen(store);

        // ---- Bulk ledger traffic (non-sale): folds into the balances but is
        // NOT part of the reported day's sales, so the report's product scan
        // stays small (a day report never walks thousands of products). ----
        var bulkMovements = new List<Movement>(BulkProducts * 6);
        for (int i = 0; i < BulkProducts; i++)
        {
            ProductId product = PerfProduct(100_000 + i);
            var goodsId = new GoodsId(Ids.Next($"bgoods-{i}"));
            var countId = new CountId(Ids.Next($"bcount-{i}"));
            var adj1 = new AdjustmentId(Ids.Next($"badj-a-{i}"));
            var adj2 = new AdjustmentId(Ids.Next($"badj-b-{i}"));
            var adj3 = new AdjustmentId(Ids.Next($"badj-c-{i}"));
            var adj4 = new AdjustmentId(Ids.Next($"badj-d-{i}"));

            store.RegisterCount(new PhysicalCount(countId, StoreLocations.Warehouse,
                new[] { new CountLine(product, Quantity.Of(47), null) }, User, t0));
            store.RegisterAdjustment(new Adjustment(adj1, Day, product, StoreLocations.Warehouse, AdjustmentKind.Decrease, "perf decrease", User, t0, User));
            store.RegisterAdjustment(new Adjustment(adj2, Day, product, StoreLocations.Warehouse, AdjustmentKind.Damage, "perf damage", User, t0, User));
            store.RegisterAdjustment(new Adjustment(adj3, Day, product, StoreLocations.Warehouse, AdjustmentKind.Increase, "perf increase", User, t0, User));
            store.RegisterAdjustment(new Adjustment(adj4, Day, product, StoreLocations.Warehouse, AdjustmentKind.Loss, "perf loss", User, t0, User));

            bulkMovements.Add(Moves.GoodsReceived(0, t0, User, new GoodsRef(goodsId), product, StoreLocations.Warehouse, Quantity.Of(100)));
            bulkMovements.Add(Moves.CountObserved(0, t0, User, new CountRef(countId), product, StoreLocations.Warehouse, Quantity.Of(47)));
            bulkMovements.Add(Moves.AdjustmentDecrease(0, t0, User, new AdjustmentRef(adj1), product, StoreLocations.Warehouse, Quantity.Of(1), "perf decrease"));
            bulkMovements.Add(Moves.Damage(0, t0, User, new AdjustmentRef(adj2), product, StoreLocations.Warehouse, Quantity.Of(1), "perf damage"));
            bulkMovements.Add(Moves.AdjustmentIncrease(0, t0, User, new AdjustmentRef(adj3), product, StoreLocations.Warehouse, Quantity.Of(1), "perf increase"));
            bulkMovements.Add(Moves.Loss(0, t0, User, new AdjustmentRef(adj4), product, StoreLocations.Warehouse, Quantity.Of(1), "perf loss"));
        }

        store.Bind();
        AppendOrThrow(store, bulkMovements, "bulk seed failed");

        // ---- Reported-day stock: a small catalog received into the warehouse. ----
        var perfStockMovements = new List<Movement>(PerfProducts);
        for (int i = 0; i < PerfProducts; i++)
        {
            ProductId product = PerfProduct(i);
            var goodsId = new GoodsId(Ids.Next($"pgoods-{i}"));
            perfStockMovements.Add(Moves.GoodsReceived(0, t0, User, new GoodsRef(goodsId), product, StoreLocations.Warehouse, Quantity.Of(2000)));
        }

        store.Bind();
        AppendOrThrow(store, perfStockMovements, "perf stock seed failed");

        // ---- Sales (20,000 movements: SaleIssue + Receipt per sale) on the reported day ----
        var saleMovements = new List<Movement>(SalesCount * 2);
        for (int i = 0; i < SalesCount; i++)
        {
            ProductId product = PerfProduct(i % PerfProducts);
            var lineId = new SaleLineId(Guid.NewGuid());
            var paymentId = new PaymentLineId(Guid.NewGuid());
            var saleId = new SaleId(Guid.NewGuid());
            var cmd = new NewSaleCommand(
                saleId, Day, $"R-PF-{i:D6}", User, When.AddHours(2),
                new[]
                {
                    new NewSaleLine(lineId, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
                },
                new[]
                {
                    new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero),
                });

            Result<SaleDraft> draft = SaleBuilder.Build(store.Context, cmd);
            if (draft.IsFailure)
            {
                throw new InvalidOperationException($"sale seed failed at {i}: {draft.Errors[0].Code}: {draft.Errors[0].Message}");
            }

            store.RegisterSale(draft.Value.Document);
            foreach (Movement movement in draft.Value.Movements)
            {
                saleMovements.Add(movement);
            }
        }

        store.Bind();
        AppendOrThrow(store, saleMovements, "sale append failed");

        int totalMovements = bulkMovements.Count + perfStockMovements.Count + saleMovements.Count;
        Console.WriteLine($"SEED: {SalesCount} sales on day {Day}; {totalMovements} movements (bulk {bulkMovements.Count} + perf stock {perfStockMovements.Count} + sales {saleMovements.Count})");
        store.Close();
        return (SalesCount, totalMovements);
    }

    private static void AppendOrThrow(SqliteLedgerStore store, IReadOnlyList<Movement> movements, string message)
    {
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(movements.ToList()));
        if (outcome.IsFailure)
        {
            throw new InvalidOperationException($"{message}: {outcome.Errors[0].Code}: {outcome.Errors[0].Message}");
        }
    }

    private static void Close(ICloseService service, int index)
    {
        Result<CloseOutcome> result = service.Close(new DayCloseCommand(
            new CloseId(Guid.NewGuid()), Day, Money.FromPesos(100_000m).Centavos, "perf counted", User, When.AddHours(3 + index)));
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"close failed: {result.Errors[0].Code}: {result.Errors[0].Message}");
        }
    }

    private static void Reopen(IReopenService service, int index)
    {
        Result<AppendReceipt> result = service.Reopen(new DayReopenCommand(
            new DayId(Guid.NewGuid()), Day, "perf reopen", User, User, When.AddHours(3 + index)));
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"reopen failed: {result.Errors[0].Code}: {result.Errors[0].Message}");
        }
    }

    private static List<double> Timed(Action action, int runs)
    {
        var samples = new List<double>(runs);
        for (int i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return samples;
    }

    private static double P95(List<double> sorted)
        => sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
