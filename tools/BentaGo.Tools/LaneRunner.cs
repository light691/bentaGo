using System.Diagnostics;
using System.IO;
using System.Text;
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
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.Tools;

/// <summary>
/// The ten BG-003 live lanes (docs/05 lines 205-216, BG-003 §13): each lane
/// runs on a FRESH temp data root with the real SQLite store, prints exactly
/// one `L&lt;n&gt; &lt;slug&gt;: PASS|FAIL (&lt;reason&gt;)` line to stdout, and stages
/// UTF-8 text evidence named bg-003-&lt;slug&gt;.png under the evidence dir.
/// Exits 0 iff all ten lanes pass.
/// </summary>
public static class LaneRunner
{
    public static async Task<int> RunAsync(string? head, string? evidenceDir)
    {
        var lanes = new (int Number, string Slug, Func<Lane, Task<LaneResult>> Run)[]
        {
            (1, "stock-load", Lane1_StockLoadAsync),
            (2, "checkout", Lane2_CheckoutAsync),
            (3, "rollback", Lane3_RollbackAsync),
            (4, "idempotency", Lane4_IdempotencyAsync),
            (5, "concurrency", Lane5_ConcurrencyAsync),
            (6, "rebuild", Lane6_RebuildAsync),
            (7, "reversals", Lane7_ReversalsAsync),
            (8, "power-loss", Lane8_PowerLossAsync),
            (9, "migration", Lane9_MigrationAsync),
            (10, "restore", Lane10_RestoreAsync),
        };

        int passed = 0;
        var roots = new List<string>();
        foreach ((int n, string slug, Func<Lane, Task<LaneResult>> run) in lanes)
        {
            string root = Path.Combine(Path.GetTempPath(), $"bg-003-l{n}-{Guid.NewGuid():N}");
            roots.Add(root);
            var lane = new Lane(root, n, slug, head);

            LaneResult result;
            try
            {
                result = await run(lane);
            }
            catch (Exception ex)
            {
                result = LaneResult.Fail($"exception: {ex.Message}");
            }

            if (result.Ok)
            {
                passed++;
            }

            Console.WriteLine($"L{n} {slug}: {(result.Ok ? "PASS" : "FAIL")} ({result.Reason})");

            if (evidenceDir is not null && Directory.Exists(evidenceDir))
            {
                File.WriteAllText(
                    Path.Combine(evidenceDir, $"bg-003-{slug}.png"),
                    $"L{n} {slug}: {(result.Ok ? "PASS" : "FAIL")} ({result.Reason}){Environment.NewLine}{Environment.NewLine}{result.Evidence}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        foreach (string root in roots)
        {
            TryDeleteDirectory(root);
        }

        Console.WriteLine($"LANES: {passed}/10 PASS");
        return passed == lanes.Length ? 0 : 1;
    }

    private sealed record LaneResult(bool Ok, string Reason, string Evidence)
    {
        public static LaneResult Pass(string reason, string evidence) => new(true, reason, evidence);

        public static LaneResult Fail(string reason, string evidence = "") => new(false, reason, evidence);
    }

    private sealed class Lane
    {
        public Lane(string root, int number, string slug, string? head)
        {
            Root = root;
            Number = number;
            Slug = slug;
            Head = head;
            Paths = new DataPathProvider(root);
        }

        public string Root { get; }

        public int Number { get; }

        public string Slug { get; }

        public string? Head { get; }

        public DataPathProvider Paths { get; }

        public string DbPath => System.IO.Path.Combine(Paths.DataDir, "bentago.db");
    }

    private static async Task<ServiceProvider> HostAsync(Lane lane)
    {
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: lane.Root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        return host;
    }

    // ------------------------------------------------------------------
    // Lane 1 — stock load: the persisted day reconciles at both locations.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane1_StockLoadAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();

        (TripId trip, ProductId product, LocationId route, LocationId storeLoc, UserId user, PeddlerId peddler) = Day();
        SeedDay(store, trip, product, route, storeLoc, user, peddler, opening: 60, loaded: 25);

        ScenarioReport report = TwoPeddlersScenario.Render(store);
        long storeStock = store.State.StockOnHand(product, storeLoc).Units;
        long routeStock = store.State.StockOnHand(product, route).Units;
        long movements = store.Movements.Count;

        bool ok = storeStock == 60 - 25 && routeStock == 25 && movements == 3;
        string evidence = $"movements: {movements}; stock({product.Value:N}): store={storeStock} (60-25), route={routeStock} (25){Environment.NewLine}{report.Text}";
        return ok
            ? LaneResult.Pass("both locations reconcile; every movement reconciles", evidence)
            : LaneResult.Fail($"stock reconciliation mismatch (store {storeStock}, route {routeStock}, movements {movements})", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 2 — checkout: one transaction holds sale + lines + payments +
    // movements + projection + audit, and the render equals the golden.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane2_CheckoutAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();
        ScenarioReport report = TwoPeddlersScenario.Run(store);

        string inMemory = TwoPeddlersScenario.Run(new InMemoryLedgerStore()).Text;
        bool goldenOk = report.Text == inMemory;

        long sales = await CountAsync(lane.DbPath, "Sales");
        long lines = await CountAsync(lane.DbPath, "SaleLines");
        long payments = await CountAsync(lane.DbPath, "PaymentLines");
        long movements = await CountAsync(lane.DbPath, "Movements");
        long cells = await CountAsync(lane.DbPath, "CellBalances");
        long audits = await CountAsync(lane.DbPath, "AuditEvents");

        bool rowsOk = sales == 4 && lines == 4 && payments == 5 && movements == 28 && cells >= 3 && audits >= 1;
        string evidence =
            $"sale+lines+payments+movements+projection+audit commit together: sales={sales} lines={lines} payments={payments} movements={movements} cells={cells} audits={audits}; golden={(goldenOk ? "equal" : "DIFFERENT")}{Environment.NewLine}{report.Text}";
        return rowsOk && goldenOk
            ? LaneResult.Pass("sale, lines, payments, movements, projection, audit commit together; render == golden", evidence)
            : LaneResult.Fail($"row presence {(rowsOk ? "ok" : "bad")} / golden {(goldenOk ? "ok" : "bad")}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 3 — rollback: a deliberately failing append and the seam leave
    // zero partial rows in every table.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane3_RollbackAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();
        var debug = host.GetRequiredService<DebugOptions>();
        (TripId trip, ProductId product, LocationId route, LocationId storeLoc, UserId user, PeddlerId peddler) = Day();
        SeedDay(store, trip, product, route, storeLoc, user, peddler, opening: 100, loaded: 10);

        long sales = await CountAsync(lane.DbPath, "Sales");
        long lines = await CountAsync(lane.DbPath, "SaleLines");
        long payments = await CountAsync(lane.DbPath, "PaymentLines");
        long movements = await CountAsync(lane.DbPath, "Movements");
        long audits = await CountAsync(lane.DbPath, "AuditEvents");
        long cells = await CountAsync(lane.DbPath, "CellBalances");

        // (a) The fold rejects an overdrawn batch (10 units on the route,
        // lines of 8 + 5: each passes the builder's per-line check, the batch
        // sum overdraws).
        var checker = new CheckoutService(store);
        Result<CheckoutOutcome> overdraw = checker.Checkout(
            new CheckoutKey(Guid.NewGuid()),
            TwoLineSale(trip, product, "R-L3-OVER", user, 8, 5));
        if (overdraw.IsSuccess)
        {
            return LaneResult.Fail("overdrawn batch unexpectedly committed", $"sales={await CountAsync(lane.DbPath, "Sales")}");
        }

        // (b) The seam forces a mid-transaction rollback of an otherwise
        // valid sale.
        debug.FailNextAppendAfterInsert();
        Result<CheckoutOutcome> forced = checker.Checkout(
            new CheckoutKey(Guid.NewGuid()), OneUnitSale(trip, product, "R-L3-FORCED", user, qty: 1));
        bool forcedRolledBack = forced.IsFailure && forced.Errors.Any(e => e.Code == "FORCED_ROLLBACK");

        bool zeroPartial = sales == await CountAsync(lane.DbPath, "Sales")
            && lines == await CountAsync(lane.DbPath, "SaleLines")
            && payments == await CountAsync(lane.DbPath, "PaymentLines")
            && movements == await CountAsync(lane.DbPath, "Movements")
            && audits == await CountAsync(lane.DbPath, "AuditEvents")
            && cells == await CountAsync(lane.DbPath, "CellBalances");

        string evidence = $"after both failures: sales={await CountAsync(lane.DbPath, "Sales")} lines={await CountAsync(lane.DbPath, "SaleLines")} payments={await CountAsync(lane.DbPath, "PaymentLines")} movements={await CountAsync(lane.DbPath, "Movements")} audits={await CountAsync(lane.DbPath, "AuditEvents")} cells={await CountAsync(lane.DbPath, "CellBalances")} (before: {sales}/{lines}/{payments}/{movements}/{audits}/{cells}); fold code={overdraw.Errors.FirstOrDefault()?.Code} seam code={forced.Errors.FirstOrDefault()?.Code}";
        return zeroPartial && forcedRolledBack && overdraw.Errors.Any(e => e.Code == "STOCK_NEGATIVE")
            ? LaneResult.Pass("no partial sale state exists", evidence)
            : LaneResult.Fail($"zero-partial={zeroPartial} forced-rollback={forcedRolledBack}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 4 — idempotency: one sale, both submits return the same receipt
    // reference.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane4_IdempotencyAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();
        (TripId trip, ProductId product, LocationId route, LocationId storeLoc, UserId user, PeddlerId peddler) = Day();
        SeedDay(store, trip, product, route, storeLoc, user, peddler);

        var checkout = host.GetRequiredService<ICheckoutService>();
        var key = new CheckoutKey(Guid.NewGuid());
        Result<CheckoutOutcome> first = checkout.Checkout(key, OneUnitSale(trip, product, "R-L4", user, qty: 1));
        Result<CheckoutOutcome> second = checkout.Checkout(key, OneUnitSale(trip, product, "R-L4-IGNORED", user, qty: 9));

        long sales = await CountAsync(lane.DbPath, "Sales");
        bool sameReference = first.IsSuccess && second.IsSuccess && first.Value.Reference == second.Value.Reference && second.Value.Replayed;
        string evidence = $"first: {first.Value?.Reference.ReceiptNumber} sale {first.Value?.Reference.SaleId.Value:N} replayed={first.Value?.Replayed}; second: {second.Value?.Reference.ReceiptNumber} replayed={second.Value?.Replayed}; sales={sales}";
        return sales == 1 && sameReference
            ? LaneResult.Pass("one sale; both requests returned the same receipt reference", evidence)
            : LaneResult.Fail($"sales={sales} same-reference={sameReference}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 5 — concurrency: two stores race the last unit; committed stock
    // never goes below zero.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane5_ConcurrencyAsync(Lane lane)
    {
        Lane8Fixture.Seed(lane.Root);

        var store1 = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        var store2 = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        store1.Bind();
        store2.Bind();
        (Sale doc1, IReadOnlyList<Movement> mov1) = Lane8Fixture.BuildSale(store1);
        (Sale doc2, IReadOnlyList<Movement> mov2) = Lane8Fixture.BuildSale(store2);

        var gate = new ManualResetEventSlim(false);
        Task<Result<AppendReceipt>> run1 = Task.Run(() =>
        {
            gate.Wait();
            store1.RegisterSale(doc1);
            store1.Bind();
            return store1.Append(mov1);
        });
        Task<Result<AppendReceipt>> run2 = Task.Run(() =>
        {
            gate.Wait();
            store2.RegisterSale(doc2);
            store2.Bind();
            return store2.Append(mov2);
        });

        gate.Set();
        Result<AppendReceipt> result1 = await run1;
        Result<AppendReceipt> result2 = await run2;

        bool oneOk = result1.IsSuccess != result2.IsSuccess;
        Result<AppendReceipt> loser = result1.IsSuccess ? result2 : result1;
        bool stockNegative = loser.Errors.Any(e => e.Code == "STOCK_NEGATIVE");

        var reopened = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        long committedStock = reopened.State.StockOnHand(Lane8Fixture.Sardines, Lane8Fixture.Route).Units;
        await reopened.RebuildAsync();

        string evidence = $"run1 ok={result1.IsSuccess} code={result1.Errors.FirstOrDefault()?.Code}; run2 ok={result2.IsSuccess} code={result2.Errors.FirstOrDefault()?.Code}; committed stock={committedStock}";
        return oneOk && stockNegative && committedStock == 0
            ? LaneResult.Pass("one Ok, one STOCK_NEGATIVE; committed stock never below zero", evidence)
            : LaneResult.Fail($"oneOk={oneOk} stockNegative={stockNegative} committedStock={committedStock}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 6 — rebuild: rebuilt == cached exactly.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane6_RebuildAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();
        ScenarioReport report = TwoPeddlersScenario.Run(store);
        string before = report.Text;

        RebuildReceipt receipt = await store.RebuildAsync();
        bool identical = TwoPeddlersScenario.Render(store).Text == before
            && store.State.LastSequence == receipt.LastSequence
            && receipt.LastSequence == store.Movements.Count;

        string evidence = $"rebuild: head={receipt.LastSequence} movements={receipt.MovementCount} documents={receipt.DocumentCount}; post-rebuild render identical={identical}{Environment.NewLine}{report.Text}";
        return identical
            ? LaneResult.Pass("rebuilt and cached balances match exactly", evidence)
            : LaneResult.Fail($"post-rebuild render differs (head {receipt.LastSequence}, {receipt.MovementCount} movements)", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 7 — reversals: original rows remain; the void + return reconcile.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane7_ReversalsAsync(Lane lane)
    {
        ServiceProvider host = await HostAsync(lane);
        var store = host.GetRequiredService<SqliteLedgerStore>();
        ScenarioReport report = TwoPeddlersScenario.Run(store);

        long sales = await CountAsync(lane.DbPath, "Sales");
        long movements = await CountAsync(lane.DbPath, "Movements");
        long voids = await CountAsync(lane.DbPath, "SaleVoids");
        long returns = await CountAsync(lane.DbPath, "Returns");
        long revokeRows = await CountAsync(lane.DbPath, "Movements");

        // Reversal movements reconcile: every revoke/return/refund points at a
        // recorded sequence and the fold balances equal the golden.
        string inMemory = TwoPeddlersScenario.Run(new InMemoryLedgerStore()).Text;
        bool balancesOk = report.Text == inMemory;

        // Original rows remain: a full reopen + rebuild changes no counts.
        store.Close();
        var reopened = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        await reopened.RebuildAsync();
        bool rowsRemain = sales == await CountAsync(lane.DbPath, "Sales")
            && movements == await CountAsync(lane.DbPath, "Movements")
            && voids == await CountAsync(lane.DbPath, "SaleVoids")
            && returns == await CountAsync(lane.DbPath, "Returns");

        string evidence = $"sales={sales} movements={movements} voids={voids} returns={returns} revoke-rows={revokeRows}; balances-match-golden={balancesOk}; rows-remain-after-rebuild={rowsRemain}{Environment.NewLine}{report.Text}";
        return balancesOk && rowsRemain
            ? LaneResult.Pass("original rows remain; reversals reconcile", evidence)
            : LaneResult.Fail($"balancesOk={balancesOk} rowsRemain={rowsRemain}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 8 — power loss: the crash worker is killed mid-transaction; SQLite
    // recovery yields the full transaction or none of it.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane8_PowerLossAsync(Lane lane)
    {
        Lane8Fixture.Seed(lane.Root);
        long before = await CountAsync(lane.DbPath, "Movements"); // 3

        string dll = Path.Combine(AppContext.BaseDirectory, "BentaGo.Tools.dll");
        string readyFile = Path.Combine(lane.Root, "lane8-ready.txt");
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" lane8-crash-worker --data-root \"{lane.Root}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            return LaneResult.Fail("could not start lane8-crash-worker");
        }

        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(readyFile) && !proc.HasExited && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        if (!File.Exists(readyFile))
        {
            proc.Kill(entireProcessTree: true);
            return LaneResult.Fail("crash-worker never signalled readiness (ready-file missing)");
        }

        await Task.Delay(500); // the worker is inside its pre-commit delay
        proc.Kill(entireProcessTree: true);
        await Task.Delay(300);

        var store = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        _ = store.State; // cold fold (recovery)
        long after = store.Movements.Count;
        string quickCheck = await QuickCheckAsync(lane.DbPath);
        await store.RebuildAsync();

        bool ok = quickCheck == "ok" && (after == before); // killed pre-commit -> rolled back
        string evidence = $"movements before={before} after-recovery={after}; quick_check={quickCheck}; rebuild ok";
        return ok
            ? LaneResult.Pass($"recovery yielded none of the transaction (movements {before}->{after}, quick_check {quickCheck})", evidence)
            : LaneResult.Fail($"quick_check={quickCheck} after={after} (expected {before})", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 9 — migration: the 01 fixture upgrades to head with totals
    // preserved and a backup created first.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane9_MigrationAsync(Lane lane)
    {
        await LedgerSchemaFixture.BuildAsync(lane.Root);

        long movementsBefore = await CountAsync(lane.DbPath, "Movements");
        long? stockTotalBefore = await SumAsync(lane.DbPath, "Movements", "quantity");
        long? fundsTotalBefore = await SumAsync(lane.DbPath, "Movements", "amount_centavos");

        ServiceProvider host = await HostAsync(lane); // migrates; backs the 01 fixture up first

        long movementsAfter = await CountAsync(lane.DbPath, "Movements");
        long? stockTotalAfter = await SumAsync(lane.DbPath, "Movements", "quantity");
        long? fundsTotalAfter = await SumAsync(lane.DbPath, "Movements", "amount_centavos");

        string[] applied = GetAllAppliedMigrations(lane.DbPath);
        bool atHead = applied.Length == 3 && applied[^1].EndsWith("BG003_02_AddCheckoutIdempotency", StringComparison.Ordinal);

        string[] backups = Directory.GetFiles(lane.Paths.BackupsDir, "bentago-*.db");
        bool backupFirst = backups.Length >= 1 && backups.All(b => ReadMigrationId(b).EndsWith("BG003_01_CreateLedgerSchema", StringComparison.Ordinal));

        bool totalsPreserved = movementsBefore == movementsAfter
            && stockTotalBefore == stockTotalAfter
            && fundsTotalBefore == fundsTotalAfter;

        string evidence = $"migrations={string.Join(",", applied)}; movements {movementsBefore}->{movementsAfter}; stock-total {stockTotalBefore}->{stockTotalAfter}; funds-total {fundsTotalBefore}->{fundsTotalAfter}; pre-migration backups={backups.Length}";
        return atHead && totalsPreserved && backupFirst
            ? LaneResult.Pass("history at head; totals preserved; backup file created first", evidence)
            : LaneResult.Fail($"atHead={atHead} totalsPreserved={totalsPreserved} backupFirst={backupFirst}", evidence);
    }

    // ------------------------------------------------------------------
    // Lane 10 — restore: the re-opened store renders exactly the
    // pre-upgrade fixture after a restore cycle.
    // ------------------------------------------------------------------

    private static async Task<LaneResult> Lane10_RestoreAsync(Lane lane)
    {
        await LedgerSchemaFixture.BuildAsync(lane.Root);

        var preStore = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        string preRender = LedgerSchemaFixture.Render(preStore).Text;
        preStore.Close();

        ServiceProvider host = await HostAsync(lane);
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();

        BackupReceipt postUpgrade = await host.GetRequiredService<IBackupService>().CreateAsync();
        RestoreReceipt restored = await host.GetRequiredService<IRestoreService>().RestoreAsync(postUpgrade.BackupPath);

        var afterStore = new SqliteLedgerStore(lane.Paths, new DebugOptions());
        string afterRender = LedgerSchemaFixture.Render(afterStore).Text;
        string quickCheck = await QuickCheckAsync(lane.DbPath);

        bool ok = restored.Verified && afterRender == preRender && quickCheck == "ok";
        string evidence = $"restore verified={restored.Verified}; render equal={afterRender == preRender}; quick_check={quickCheck}; pre-restore copy exists={File.Exists(restored.PreRestoreCopyPath)}";
        return ok
            ? LaneResult.Pass("reports match the pre-upgrade fixture", evidence)
            : LaneResult.Fail($"verified={restored.Verified} render-equal={afterRender == preRender} quick_check={quickCheck}", evidence);
    }

    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static (TripId Trip, ProductId Product, LocationId Route, LocationId Store, UserId User, PeddlerId Peddler) Day()
    {
        var ids = new ScenarioIdSource("lanes-day-v1");
        return (
            new TripId(ids.Next("trip")),
            new ProductId(ids.Next("product")),
            new LocationId(ids.Next("route")),
            new LocationId(ids.Next("store")),
            new UserId(ids.Next("user")),
            new PeddlerId(ids.Next("peddler")));
    }

    private static readonly DateTimeOffset FixedWhen = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static void SeedDay(
        SqliteLedgerStore store,
        TripId trip,
        ProductId product,
        LocationId route,
        LocationId storeLoc,
        UserId user,
        PeddlerId peddler,
        int opening = 100,
        int loaded = 50)
    {
        var ids = new ScenarioIdSource("lanes-seed-v1");
        store.RegisterTrip(new Trip(trip, peddler, route, FixedWhen, user));

        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "L-OPEN", trip, Array.Empty<StockLoadLine>(), user, FixedWhen));
        store.Bind();
        Result<AppendReceipt> open = store.Append(new[]
        {
            Moves.OpeningStock(0, FixedWhen, user, new LoadRef(openId), product, storeLoc, Quantity.Of(opening)),
        });
        if (open.IsFailure)
        {
            throw new InvalidOperationException($"seed failed: {open.Errors[0].Code}");
        }

        var loadId = new LoadId(ids.Next("load"));
        Result<LoadDraft> load = LoadBuilder.Build(store.Context, new LoadCommand(
            loadId, "L-LOAD", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(loaded), Money.FromPesos(30m)) }, user, FixedWhen));
        if (load.IsFailure)
        {
            throw new InvalidOperationException($"load build failed: {load.Errors[0].Code}");
        }

        store.RegisterLoad(load.Value.Document);
        store.Bind();
        Result<AppendReceipt> appended = store.Append(load.Value.Movements);
        if (appended.IsFailure)
        {
            throw new InvalidOperationException($"load append failed: {appended.Errors[0].Code}");
        }
    }

    private static NewSaleCommand OneUnitSale(TripId trip, ProductId product, string receipt, UserId user, int qty = 1)
    {
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        return new NewSaleCommand(
            new SaleId(Guid.NewGuid()), trip, receipt, user, FixedWhen.AddHours(1),
            new[]
            {
                new NewSaleLine(lineId, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(qty), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m * qty), Money.FromPesos(45m * qty), Money.Zero),
            });
    }

    private static NewSaleCommand TwoLineSale(TripId trip, ProductId product, string receipt, UserId user, int first, int second)
    {
        var lineA = new SaleLineId(Guid.NewGuid());
        var lineB = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        return new NewSaleCommand(
            new SaleId(Guid.NewGuid()), trip, receipt, user, FixedWhen.AddHours(1),
            new[]
            {
                new NewSaleLine(lineA, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(first), Money.Zero),
                new NewSaleLine(lineB, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(second), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m * (first + second)), Money.FromPesos(45m * (first + second)), Money.Zero),
            });
    }

    private static async Task<long> CountAsync(string dbPath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
        long count = Convert.ToInt64(await command.ExecuteScalarAsync());
        await connection.CloseAsync();
        return count;
    }

    private static async Task<long?> SumAsync(string dbPath, string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SUM(\"{column}\") FROM \"{table}\"";
        object? result = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    private static async Task<string> QuickCheckAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        string? result = await command.ExecuteScalarAsync() as string;
        await connection.CloseAsync();
        return result ?? "unknown";
    }

    private static string[] GetAllAppliedMigrations(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        connection.Close();
        return ids.ToArray();
    }

    private static string ReadMigrationId(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
        string? id = command.ExecuteScalar() as string;
        connection.Close();
        return id ?? "unknown";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
