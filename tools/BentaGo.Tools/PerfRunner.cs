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
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.Tools;

/// <summary>
/// The BG-003 perf harness (docs/05 lines 218-223, BG-003 §13 perf gate):
/// baselines FIRST — (a) the trunk empty-transaction median and (b) the
/// BG-002 in-memory scenario median — then a seeded file with 10,000 distinct
/// ProductIds and 100,000 movements. P95 checkout must be ≤ 500 ms (sampled
/// after ONE warm checkout; the cold first touch is recorded separately) and
/// ledger rebuild ≤ 2 s.
/// </summary>
public static class PerfRunner
{
    private const int ProductCount = 10_000;
    private const int MovementsPerProduct = 6;
    private const int WarmCheckouts = 1;
    private const int SampledCheckouts = 100;

    private static readonly ScenarioIdSource Ids = new("perf-v1");

    private static readonly UserId User = new(Ids.Next("user"));

    private static readonly DateTimeOffset When = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly DayKey Day = DayKey.FromUtc(When);

    /// <summary>
    /// Registers the trading day (movement-less day document) and rebinds, so
    /// sale builders see the day as open. Idempotent per store instance.
    /// </summary>
    private static void EnsureDayOpen(ILedgerStore store)
    {
        store.RegisterDayOpen(new DayOpen(new DayId(Ids.Next("day")), Day, 0, User, When));
        store.Bind();
    }

    /// <summary>
    /// Baseline (b): a minimal in-memory day (open day, goods receipt, one
    /// sale) through the real builders — the scenario-median workload.
    /// </summary>
    private static void RunInMemoryDay()
    {
        var store = new InMemoryLedgerStore();
        EnsureDayOpen(store);
        ProductId product = PerfProduct(0);
        var goodsId = new GoodsId(Ids.Next("goods"));
        var saleId = new SaleId(Ids.Next("sale"));
        var lineId = new SaleLineId(Ids.Next("line"));
        var paymentId = new PaymentLineId(Ids.Next("pay"));
        _ = store.Append(new[]
        {
            Moves.GoodsReceived(0, When, User, new GoodsRef(goodsId), product, StoreLocations.Warehouse, Quantity.Of(50)),
        });
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            saleId, Day, "R-PERF", User, When.AddHours(1),
            new[] { new NewSaleLine(lineId, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(1), Money.Zero) },
            new[] { new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero) }));
        if (draft.IsFailure)
        {
            throw new InvalidOperationException($"in-memory baseline sale failed: {draft.Errors[0].Code}");
        }

        store.RegisterSale(draft.Value.Document);
        _ = store.Append(draft.Value.Movements);
    }

    /// <summary>
    /// Product id derivable per index WITHOUT the shared counter (the seed
    /// and the sampled checkouts must agree on the same id).
    /// </summary>
    private static ProductId PerfProduct(int index) =>
        new(new ScenarioIdSource($"perf-product-{index}").Next("p"));

    public static async Task<int> RunAsync(string root)
    {
        Console.WriteLine("PERF: recording baselines FIRST (docs/05 line 222)");

        double emptyTxnMedianMs = await EmptyTransactionMedianMsAsync();
        Console.WriteLine($"BASELINE_EMPTY_TXN_MEDIAN_MS: {emptyTxnMedianMs:F2}");

        double inMemoryScenarioMedianMs = Median(
            Enumerable.Range(0, 7).Select(unused =>
            {
                var stopwatch = Stopwatch.StartNew();
                RunInMemoryDay();
                stopwatch.Stop();
                return stopwatch.Elapsed.TotalMilliseconds;
            }));
        Console.WriteLine($"BASELINE_INMEMORY_SCENARIO_MEDIAN_MS: {inMemoryScenarioMedianMs:F2}");

        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        string dbPath = Path.Combine(host.GetRequiredService<IDataPathProvider>().DataDir, "bentago.db");

        var seedWatch = Stopwatch.StartNew();
        await SeedAsync(dbPath);
        seedWatch.Stop();
        Console.WriteLine($"SEED: {ProductCount * MovementsPerProduct} movements / {ProductCount} products in {seedWatch.ElapsedMilliseconds} ms");

        // Cold: the first checkout on a FRESH store (includes the one-shot
        // full re-fold) — recorded, not sampled (R7 decision).
        double coldMs = await ColdCheckoutMsAsync(dbPath);
        Console.WriteLine($"COLD_CHECKOUT_MS: {coldMs:F2} (first checkout after open, includes the initial re-fold)");

        var p95 = await WarmP95CheckoutMsAsync(host, dbPath);
        Console.WriteLine($"WARM_P95_CHECKOUT_MS: {p95:F2} (sampled after {WarmCheckouts} warm checkout(s))");

        var store = host.GetRequiredService<SqliteLedgerStore>();
        var rebuildWatch = Stopwatch.StartNew();
        RebuildReceipt receipt = await store.RebuildAsync();
        rebuildWatch.Stop();
        Console.WriteLine($"REBUILD_MS: {rebuildWatch.ElapsedMilliseconds} (head {receipt.LastSequence}, {receipt.MovementCount} movements, {receipt.DocumentCount} documents)");

        bool p95Ok = p95 <= 500;
        bool rebuildOk = rebuildWatch.ElapsedMilliseconds <= 2000;

        Console.WriteLine($"PERF: {(p95Ok && rebuildOk ? "PASS" : "FAIL")} (P95 <= 500 ms: {p95Ok}; rebuild <= 2 s: {rebuildOk})");
        return p95Ok && rebuildOk ? 0 : 1;
    }

    /// <summary>Baseline (a): the trunk empty-transaction median on the pinned host.</summary>
    private static async Task<double> EmptyTransactionMedianMsAsync()
    {
        string db = Path.Combine(Path.GetTempPath(), $"bg003-perf-empty-{Guid.NewGuid():N}.db");
        string[] times = new string[15];
        try
        {
            for (int i = 0; i < 15; i++)
            {
                await using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
                await connection.OpenAsync();
                var stopwatch = Stopwatch.StartNew();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "BEGIN IMMEDIATE";
                    command.ExecuteNonQuery();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "COMMIT";
                    command.ExecuteNonQuery();
                }

                stopwatch.Stop();
                times[i] = stopwatch.Elapsed.TotalMilliseconds.ToString("F4");
                await connection.CloseAsync();
            }
        }
        finally
        {
            if (File.Exists(db))
            {
                File.Delete(db);
            }
        }

        return Median(times.Select(double.Parse));
    }

    private static async Task SeedAsync(string dbPath)
    {
        var paths = new DataPathProvider(Path.GetDirectoryName(Path.GetDirectoryName(dbPath))!);
        var store = new SqliteLedgerStore(paths, new DebugOptions());

        EnsureDayOpen(store);

        var movements = new List<Movement>(ProductCount * MovementsPerProduct);
        var products = Enumerable.Range(0, ProductCount).Select(PerfProduct).ToArray();

        for (int i = 0; i < ProductCount; i++)
        {
            ProductId product = products[i];
            string number = $"PERF-{i:D6}";
            var goodsId = new GoodsId(Ids.Next($"goods-{i}"));
            var countId = new CountId(Ids.Next($"count-{i}"));
            var adj1 = new AdjustmentId(Ids.Next($"adj-a-{i}"));
            var adj2 = new AdjustmentId(Ids.Next($"adj-b-{i}"));
            var adj3 = new AdjustmentId(Ids.Next($"adj-c-{i}"));
            var adj4 = new AdjustmentId(Ids.Next($"adj-d-{i}"));

            store.RegisterCount(new PhysicalCount(countId, StoreLocations.Warehouse,
                new[] { new CountLine(product, Quantity.Of(47), null) }, User, When));
            store.RegisterAdjustment(new Adjustment(adj1, Day, product, StoreLocations.Warehouse, AdjustmentKind.Decrease, "perf decrease", User, When, User));
            store.RegisterAdjustment(new Adjustment(adj2, Day, product, StoreLocations.Warehouse, AdjustmentKind.Damage, "perf damage", User, When, User));
            store.RegisterAdjustment(new Adjustment(adj3, Day, product, StoreLocations.Warehouse, AdjustmentKind.Increase, "perf increase", User, When, User));
            store.RegisterAdjustment(new Adjustment(adj4, Day, product, StoreLocations.Warehouse, AdjustmentKind.Loss, "perf loss", User, When, User));

            movements.Add(Moves.GoodsReceived(0, When, User, new GoodsRef(goodsId), product, StoreLocations.Warehouse, Quantity.Of(100)));
            movements.Add(Moves.CountObserved(0, When, User, new CountRef(countId), product, StoreLocations.Warehouse, Quantity.Of(47)));
            movements.Add(Moves.AdjustmentDecrease(0, When, User, new AdjustmentRef(adj1), product, StoreLocations.Warehouse, Quantity.Of(1), "perf decrease"));
            movements.Add(Moves.Damage(0, When, User, new AdjustmentRef(adj2), product, StoreLocations.Warehouse, Quantity.Of(1), "perf damage"));
            movements.Add(Moves.AdjustmentIncrease(0, When, User, new AdjustmentRef(adj3), product, StoreLocations.Warehouse, Quantity.Of(1), "perf increase"));
            movements.Add(Moves.Loss(0, When, User, new AdjustmentRef(adj4), product, StoreLocations.Warehouse, Quantity.Of(1), "perf loss"));
        }

        store.Bind();

        var stopwatch = Stopwatch.StartNew();
        Result<AppendOutcome> appended = store.Append(new AppendBatch(movements));
        stopwatch.Stop();
        if (appended.IsFailure)
        {
            throw new InvalidOperationException($"perf seed failed: {appended.Errors[0].Code}: {appended.Errors[0].Message}");
        }

        Console.WriteLine($"SEED_APPEND_MS: {stopwatch.ElapsedMilliseconds}");
        store.Close();
    }

    private static async Task<double> ColdCheckoutMsAsync(string dbPath)
    {
        var paths = new DataPathProvider(Path.GetDirectoryName(Path.GetDirectoryName(dbPath))!);
        var store = new SqliteLedgerStore(paths, new DebugOptions());
        var checkout = new CheckoutService(store);
        EnsureDayOpen(store);

        var stopwatch = Stopwatch.StartNew();
        Result<CheckoutOutcome> outcome = checkout.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            PerfSaleCommand("R-COLD", 0));
        stopwatch.Stop();
        store.Close();

        if (outcome.IsFailure)
        {
            throw new InvalidOperationException($"cold checkout failed: {outcome.Errors[0].Code}");
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> WarmP95CheckoutMsAsync(ServiceProvider host, string dbPath)
    {
        var store = host.GetRequiredService<SqliteLedgerStore>();
        var checkout = host.GetRequiredService<ICheckoutService>();
        EnsureDayOpen(store);

        for (int i = 0; i < WarmCheckouts; i++)
        {
            Result<CheckoutOutcome> warm = checkout.Checkout(
                new CheckoutKey(Guid.NewGuid()), PerfSaleCommand($"R-WARM-{i}", i));
            if (warm.IsFailure)
            {
                throw new InvalidOperationException($"warm checkout failed: {warm.Errors[0].Code}");
            }
        }

        var samples = new List<double>(SampledCheckouts);
        for (int i = 0; i < SampledCheckouts; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            Result<CheckoutOutcome> outcome = checkout.Checkout(
                new CheckoutKey(Guid.NewGuid()), PerfSaleCommand($"R-SMP-{i}", i));
            stopwatch.Stop();
            if (outcome.IsFailure)
            {
                throw new InvalidOperationException($"sampled checkout failed: {outcome.Errors[0].Code}");
            }

            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[(int)(samples.Count * 0.95) - 1]; // 95th percentile
    }

    private static NewSaleCommand PerfSaleCommand(string receipt, int index)
    {
        ProductId product = PerfProduct(index % ProductCount);
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        return new NewSaleCommand(
            new SaleId(Guid.NewGuid()), Day, receipt, User, When.AddHours(2),
            new[]
            {
                new NewSaleLine(lineId, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero),
            });
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
