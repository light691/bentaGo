using System.IO;
using System.Security.Cryptography;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (commandLine.Length == 0 || commandLine[0] is "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

try
{
    return commandLine[0] switch
    {
        "status" => RunStatus(),
        "scenario" when commandLine.Length > 1 && (commandLine[1] is "two-peddlers" or "trip-lifecycle") => await RunScenarioAsync(commandLine[1], commandLine.Skip(2).ToArray()),
        "ledger" when commandLine.Length > 1 => await RunLedgerAsync(commandLine.Skip(1).ToArray()),
        "lane8-crash-worker" => await RunLane8CrashWorkerAsync(commandLine.Skip(1).ToArray()),
        "lanes" when commandLine.Length > 1 && commandLine[1] == "bg-003" => await RunLanesAsync(commandLine.Skip(2).ToArray()),
        "perf" when commandLine.Length > 1 && commandLine[1] == "bg-003" => await RunPerfAsync(commandLine.Skip(2).ToArray()),
        "perf" when commandLine.Length > 1 && commandLine[1] == "bg-007" => await RunPrintPerfAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "migrate" => await RunMigrateAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "backup" => await RunBackupAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "restore" => await RunRestoreAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "verify" => await RunVerifyAsync(),
        "bg006-lane-seed" when commandLine.Length > 1 => await RunBg006LaneSeedAsync(commandLine.Skip(1).ToArray()),
        _ => UnknownCommand(commandLine),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        BentaGo.Tools - foundation CLI for the BentaGo host.

        Commands:
          status                         Print data root, database path, schema version, database sha256.
          scenario two-peddlers|trip-lifecycle
                                         Print the deterministic business day.
                                           two-peddlers: the BG-002/3 two-peddler day.
                                           trip-lifecycle: BG-005c open -> load -> sell -> close
                                           plus a cancel-bearing variant (loads revert) and the
                                           peddler-scoped on-hand read model.
                                           Options: --persist (run against SQLite), --data-root <dir>
          ledger load|checkout|void|return|adjustment|count|close|reopen|cancel|rebuild [options]
                                         Run one business operation against the SQLite ledger. Common
                                         options: --data-root <dir>, --by <user-id>, --when <utc-iso>.
                                         load also needs --peddler (scope guard). cancel needs
                                         --trip <id> --peddler <id> --reason <text>.
                                         checkout extra options: --key <checkout-key> --trip <id> --receipt <n>
                                           --product <id> --qty <n> --price <pesos> --method <Cash|Gcash|Maya|BankTransfer|Credit>
                                           --fail-before-commit (L3 seam) --crash-after-write (L8 seam)
          lane8-crash-worker             Lane-8 crash child: sells the last unit in a transaction delayed
                                         before commit; parent kills it by PID. Options: --data-root <dir>
          lanes bg-003                   Run all ten BG-003 live lanes on fresh temp roots.
                                          Options: --head <sha>, --evidence-dir <dir> (writes bg-003-*.png)
          perf bg-003                    BG-003 perf harness: baselines first, then P95 checkout and
                                         rebuild over a 10k-product / 100k-movement seeded file.
                                          Options: --data-root <dir>
          foundation migrate             Migrate the database schema to the latest version.
                                          Options: --data-root <dir> (default: BENTAGO_DATA_ROOT or a fresh temp dir)
          foundation backup              Create a verified backup of the active database.
                                          Options: --data-root <dir>
          foundation restore <path>      Restore the active database from <path> (validated, refused on mismatch).
                                          Options: --data-root <dir>
          foundation verify              End-to-end proof: migrate, seed, backup, restore, verify.
                                          Runs entirely under a fresh temp data root and exits 0 only if all checks pass.
          help                           Show this text.
          bg006-lane-seed --root <dir>   Seed an isolated BentaGo data root for the BG-006 sales
                                           demo recording: open trip with 50 Sardines/Kopi on the
                                           route, catalog products, and the seller/seller user
                                           (mirrors the tests/AppShell SalesSeed scenario).
        """);
}

static async Task<int> RunScenarioAsync(string scenarioName, string[] args)
{
    bool persist = args.Contains("--persist", StringComparer.Ordinal);
    string root = ResolveDataRoot(args);

    Func<ILedgerStore, ScenarioReport> run = scenarioName == "two-peddlers"
        ? TwoPeddlersScenario.Run
        : TripLifecycleScenario.Run;
    Func<ILedgerStore, ScenarioReport> render = scenarioName == "two-peddlers"
        ? TwoPeddlersScenario.Render
        : TripLifecycleScenario.Render;

    if (!persist)
    {
        var store = new InMemoryLedgerStore();
        ScenarioReport report = run(store);
        Console.Write(report.Text);
        return 0;
    }

    try
    {
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();

        // Run the full deterministic day against SQLite.
        var store1 = host.GetRequiredService<SqliteLedgerStore>();
        ScenarioReport report1 = run(store1);
        Console.Write(report1.Text);
        Console.WriteLine();

        // Byte-identity with the in-memory golden (C14).
        var memory = new InMemoryLedgerStore();
        string inMemory = run(memory).Text;
        bool goldenIdentical = report1.Text == inMemory;
        Console.WriteLine($"GOLDEN_IDENT:   {(goldenIdentical ? "PASS" : "FAIL")} (persisted vs in-memory)");

        // Restart: close store1, reopen a fresh store over the same file —
        // the fold must reproduce the persisted day without re-running it.
        store1.Close();
        var paths = host.GetRequiredService<IDataPathProvider>();
        var store2 = new SqliteLedgerStore(paths, host.GetRequiredService<DebugOptions>());
        string afterRestart = render(store2).Text;
        bool restartOk = afterRestart == report1.Text;
        Console.WriteLine($"RESTART:        {(restartOk ? "PASS" : "FAIL")} (render after reopen == first run)");

        // Rebuild: full fold + projection rewrite; render must stay identical.
        RebuildReceipt rebuilt = await store2.RebuildAsync();
        bool rebuildOk = render(store2).Text == report1.Text;
        Console.WriteLine($"REBUILD:        {(rebuildOk ? "PASS" : "FAIL")} (head {rebuilt.LastSequence}, {rebuilt.MovementCount} movements, {rebuilt.DocumentCount} documents)");

        return goldenIdentical && restartOk && rebuildOk ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex}");
        return 1;
    }
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        string key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            options[key] = args[i + 1];
            i++;
        }
        else
        {
            options[key] = "true";
        }
    }

    return options;
}

static string? Opt(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out string? value) && value != "true" ? value : null;

static Guid OptGuid(Dictionary<string, string> options, string key, string usage)
{
    string? value = Opt(options, key);
    if (value is null || !Guid.TryParse(value, out Guid parsed))
    {
        throw new InvalidOperationException($"missing or invalid --{key} ({usage})");
    }

    return parsed;
}

static string RequireOpt(Dictionary<string, string> options, string key)
{
    string? value = Opt(options, key);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"missing --{key}");
    }

    return value;
}

static UserId OptUser(Dictionary<string, string> options)
{
    string? by = Opt(options, "by");
    return new UserId(by is not null && Guid.TryParse(by, out Guid parsed) ? parsed : Guid.NewGuid());
}

static DateTimeOffset OptWhen(Dictionary<string, string> options)
{
    string? when = Opt(options, "when");
    if (when is null)
    {
        return DateTimeOffset.UtcNow;
    }

    return DateTimeOffset.Parse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}

static async Task<int> RunLedgerAsync(string[] args)
{
    if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
    {
        Console.Error.WriteLine("usage: ledger load|checkout|void|return|adjustment|count|close|reopen|cancel|rebuild [options]");
        return 1;
    }

    string op = args[0];
    Dictionary<string, string> opts = ParseOptions(args.Skip(1).ToArray());
    string root = Opt(opts, "data-root")
        ?? Environment.GetEnvironmentVariable("BENTAGO_DATA_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bentago-ledger-" + Guid.NewGuid().ToString("N"));

    try
    {
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var store = host.GetRequiredService<SqliteLedgerStore>();
        var debug = host.GetRequiredService<DebugOptions>();
        UserId by = OptUser(opts);
        DateTimeOffset when = OptWhen(opts);

        switch (op)
        {
            case "opening-stock":
                {
                    // Pure OpeningStock against an opening load document with no
                    // lines (scenario precedent): stock into a location.
                    var loadId = new LoadId(Guid.NewGuid());
                    var load = new StockLoad(loadId, "OPEN-" + Guid.NewGuid().ToString("N")[..8], default, Array.Empty<StockLoadLine>(), by, when);
                    store.RegisterLoad(load);
                    store.Bind();
                    Result<AppendReceipt> open = store.Append(new[]
                    {
                    Moves.OpeningStock(0, when, by, new LoadRef(loadId),
                        new ProductId(OptGuid(opts, "product", "product id")),
                        new LocationId(OptGuid(opts, "location", "location id")),
                        Quantity.Of(int.Parse(RequireOpt(opts, "qty")))),
                });
                    if (open.IsFailure)
                    {
                        Console.Error.WriteLine($"OPENING-STOCK: FAIL ({open.Errors[0].Code}: {open.Errors[0].Message})");
                        return 1;
                    }

                    Console.WriteLine($"OPENING-STOCK: seq {open.Value.FirstSequence}-{open.Value.LastSequence}");
                    return 0;
                }

            case "open-trip":
                {
                    string? tripOpt = Opt(opts, "trip");
                    var tripId = new TripId(tripOpt is not null && Guid.TryParse(tripOpt, out Guid parsedTrip) ? parsedTrip : Guid.NewGuid());
                    Result<Trip> result = host.GetRequiredService<IOpenTripService>().OpenTrip(new NewTripCommand(
                        tripId,
                        new PeddlerId(OptGuid(opts, "peddler", "peddler id")),
                        new LocationId(OptGuid(opts, "location", "route location id")),
                        by, when));
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"OPEN-TRIP: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"OPEN-TRIP: {tripId.Value:N}");
                    return 0;
                }

            case "load":
                {
                    var loadId = new LoadId(Guid.NewGuid());
                    var command = new LoadCommand(
                        loadId,
                        RequireOpt(opts, "number"),
                        new TripId(OptGuid(opts, "trip", "trip id")),
                        new LocationId(OptGuid(opts, "from", "source location id")),
                        new[]
                        {
                        new LoadLine(
                            new ProductId(OptGuid(opts, "product", "product id")),
                            Opt(opts, "sku") ?? "SKU",
                            Opt(opts, "name") ?? "Product",
                            Quantity.Of(int.Parse(RequireOpt(opts, "qty"))),
                            Money.FromPesos(decimal.TryParse(Opt(opts, "cost"), out decimal cost) ? cost : 0m)),
                        },
                        by, when);
                    Result<LoadOutcome> result = host.GetRequiredService<ILoadService>().Load(
                        command,
                        new PeddlerId(OptGuid(opts, "peddler", "peddler id (the load's trip must belong to it)")));
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"LOAD: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"LOAD:      {command.LoadNumber} seq {result.Value.Receipt.FirstSequence}-{result.Value.Receipt.LastSequence}");
                    return 0;
                }

            case "checkout":
                {
                    bool failBeforeCommit = opts.ContainsKey("fail-before-commit");
                    bool crashAfterWrite = opts.ContainsKey("crash-after-write");
                    if (failBeforeCommit)
                    {
                        debug.FailNextAppendAfterInsert();
                    }

                    if (crashAfterWrite)
                    {
                        debug.DelayBeforeCommit();
                    }

                    if (!Enum.TryParse<PaymentMethod>(RequireOpt(opts, "method"), out PaymentMethod method))
                    {
                        throw new InvalidOperationException($"unknown payment method '--method {opts["method"]}'");
                    }

                    decimal price = decimal.Parse(RequireOpt(opts, "price"));
                    int qty = int.Parse(RequireOpt(opts, "qty"));
                    var saleId = new SaleId(Guid.NewGuid());
                    var lineId = new SaleLineId(Guid.NewGuid());
                    var paymentId = new PaymentLineId(Guid.NewGuid());
                    var command = new NewSaleCommand(
                        saleId,
                        new TripId(OptGuid(opts, "trip", "trip id")),
                        RequireOpt(opts, "receipt"),
                        by, when,
                        new[]
                        {
                        new NewSaleLine(lineId, new ProductId(OptGuid(opts, "product", "product id")),
                            Opt(opts, "sku") ?? "SKU", Opt(opts, "name") ?? "Product",
                            Money.FromPesos(price), Quantity.Of(qty), Money.Zero),
                        },
                        method == PaymentMethod.Cash
                            ? new[]
                            {
                            new NewPaymentLine(paymentId, method, "", Money.FromPesos(price * qty), Money.FromPesos(price * qty), Money.Zero),
                            }
                            : method == PaymentMethod.Credit
                                ? new[] { NewPaymentLine.Credit(paymentId, RequireOpt(opts, "reference"), Money.FromPesos(price * qty)) }
                                : new[] { new NewPaymentLine(paymentId, method, "", Money.FromPesos(price * qty), Money.Zero, Money.Zero) });
                    var key = new CheckoutKey(OptGuid(opts, "key", "checkout idempotency key"));
                    Result<CheckoutOutcome> result = host.GetRequiredService<ICheckoutService>().Checkout(key, command);
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"CHECKOUT: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"CHECKOUT:  {result.Value.Reference.ReceiptNumber} sale {result.Value.Reference.SaleId.Value:N} replayed {result.Value.Replayed} seq {result.Value.Receipt?.FirstSequence}-{result.Value.Receipt?.LastSequence}");

                    // C5: print AFTER commit, outside the sale transaction.
                    if (!result.Value.Replayed)
                    {
                        Sale? sale = store.Context.FindSale(result.Value.Reference.SaleId);
                        if (sale is not null)
                        {
                            PrintTicketResult print = await host.GetRequiredService<IPrintTicketStore>().PrintTicketAsync(sale);
                            Console.WriteLine($"PRINT:     {(print.Printed ? "OK" : $"FAILED ({print.FailureReason})")}");
                        }
                    }

                    return 0;
                }

            case "void":
                {
                    var voidId = new VoidId(Guid.NewGuid());
                    UserId? approver = opts.ContainsKey("approved-by") ? new UserId(OptGuid(opts, "approved-by", "approver user id")) : null;
                    Result<VoidOutcome> result = host.GetRequiredService<IVoidService>().Void(new VoidCommand(
                        voidId, new SaleId(OptGuid(opts, "sale", "sale id")), RequireOpt(opts, "reason"), by, when, approver));
                    return PrintSimple(result, "VOID", $"sale {opts["sale"]}");
                }

            case "return":
                {
                    var returnId = new ReturnId(Guid.NewGuid());
                    Result<ReturnOutcome> result = host.GetRequiredService<IReturnService>().Return(new ReturnCommand(
                        returnId,
                        new SaleId(OptGuid(opts, "sale", "sale id")),
                        new[]
                        {
                        new ReturnLineCommand(
                            new SaleLineId(OptGuid(opts, "line", "sale line id")),
                            Quantity.Of(int.Parse(RequireOpt(opts, "qty"))),
                            RequireOpt(opts, "reason"),
                            Enum.Parse<PaymentMethod>(RequireOpt(opts, "refund-method")),
                            Money.FromPesos(decimal.Parse(RequireOpt(opts, "refund")))),
                        },
                        by, when));
                    return PrintSimple(result, "RETURN", $"sale {opts["sale"]}");
                }

            case "adjustment":
                {
                    UserId? approver = opts.ContainsKey("approved-by") ? new UserId(OptGuid(opts, "approved-by", "approver user id")) : null;
                    var adjustmentId = new AdjustmentId(Guid.NewGuid());
                    Result<AdjustmentOutcome> result = host.GetRequiredService<IAdjustmentService>().Adjust(new AdjustmentCommand(
                        adjustmentId,
                        new TripId(OptGuid(opts, "trip", "trip id")),
                        new ProductId(OptGuid(opts, "product", "product id")),
                        new LocationId(OptGuid(opts, "location", "location id")),
                        Enum.Parse<AdjustmentKind>(RequireOpt(opts, "kind")),
                        Quantity.Of(int.Parse(RequireOpt(opts, "qty"))),
                        RequireOpt(opts, "reason"),
                        by, when, approver));
                    return PrintSimple(result, "ADJUSTMENT", opts["kind"]);
                }

            case "count":
                {
                    var countId = new CountId(Guid.NewGuid());
                    Result<CountOutcome> result = host.GetRequiredService<ICountService>().Count(new CountCommand(
                        countId,
                        new LocationId(OptGuid(opts, "location", "location id")),
                        new[] { new CountLineCommand(new ProductId(OptGuid(opts, "product", "product id")), Quantity.Of(int.Parse(RequireOpt(opts, "observed"))), null) },
                        by, when));
                    return PrintSimple(result, "COUNT", $"location {opts["location"]}");
                }

            case "close":
                {
                    var closeId = new CloseId(Guid.NewGuid());
                    Result<CloseOutcome> result = host.GetRequiredService<ICloseService>().Close(new CloseCommand(
                        closeId,
                        new TripId(OptGuid(opts, "trip", "trip id")),
                        Money.FromPesos(decimal.Parse(RequireOpt(opts, "counted"))),
                        Opt(opts, "notes"),
                        by, when));
                    return PrintSimple(result, "CLOSE", $"trip {opts["trip"]}");
                }

            case "reopen":
                {
                    Result<AppendReceipt> result = host.GetRequiredService<IReopenService>().Reopen(new ReopenCommand(
                        new TripId(OptGuid(opts, "trip", "trip id")), RequireOpt(opts, "reason"), by, when));
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"REOPEN: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"REOPEN:    ok seq {result.Value.FirstSequence}-{result.Value.LastSequence}");
                    return 0;
                }

            case "cancel":
                {
                    Result<CancelOutcome> result = host.GetRequiredService<ICancelTripService>().Cancel(
                        new CancelTripCommand(
                            new TripId(OptGuid(opts, "trip", "trip id")),
                            RequireOpt(opts, "reason"),
                            by, when),
                        new PeddlerId(OptGuid(opts, "peddler", "peddler id (the load's trip must belong to it)")));
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"CANCEL: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"CANCEL:    ok seq {result.Value.Receipt.FirstSequence}-{result.Value.Receipt.LastSequence}");
                    return 0;
                }

            case "rebuild":
                {
                    RebuildReceipt receipt = await store.RebuildAsync();
                    Console.WriteLine($"REBUILD:   head {receipt.LastSequence} movements {receipt.MovementCount} documents {receipt.DocumentCount}");
                    return 0;
                }

            default:
                Console.Error.WriteLine($"ERROR: unknown ledger op '{op}'");
                return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static int PrintSimple<T>(Result<T> result, string label, string what)
{
    if (result.IsFailure)
    {
        Console.Error.WriteLine($"{label}: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
        return 1;
    }

    Console.WriteLine($"{label}:    {what} ok");
    return 0;
}

/// <summary>
/// Lane-8 crash child (docs/05 lane 8): opens the parent-seeded root, binds,
/// builds the last-unit sale, and appends it inside a transaction delayed
/// BEFORE COMMIT. The ready-file is written mid-transaction (post-insert,
/// pre-commit), then the parent kills this process by PID.
/// </summary>
static async Task<int> RunLane8CrashWorkerAsync(string[] args)
{
    Dictionary<string, string> opts = ParseOptions(args);
    string? root = Opt(opts, "data-root");
    if (root is null)
    {
        Console.Error.WriteLine("lane8-crash-worker requires --data-root <dir>");
        return 1;
    }

    string readyFile = Path.Combine(root, "lane8-ready.txt");
    ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
    await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

    var store = host.GetRequiredService<SqliteLedgerStore>();
    var debug = host.GetRequiredService<DebugOptions>();

    store.Bind();
    (Sale document, IReadOnlyList<Movement> movements) = Lane8Fixture.BuildSale(store);
    store.RegisterSale(document);
    store.Bind();

    debug.DelayBeforeCommit();
    debug.PreCommit = () => File.WriteAllText(readyFile, $"pid {Environment.ProcessId}\n");

    Result<AppendReceipt> outcome = store.Append(movements);
    if (outcome.IsFailure)
    {
        Console.WriteLine($"WORKER: append failed {outcome.Errors[0].Code}");
        return 1;
    }

    Console.WriteLine($"WORKER: committed seq {outcome.Value.FirstSequence}-{outcome.Value.LastSequence}");
    return 0;
}

static async Task<int> RunLanesAsync(string[] args)
{
    Dictionary<string, string> opts = ParseOptions(args);
    string? head = Opt(opts, "head");
    string? evidenceDir = Opt(opts, "evidence-dir");

    if (evidenceDir is not null)
    {
        Directory.CreateDirectory(evidenceDir);
    }

    return await BentaGo.Tools.LaneRunner.RunAsync(head, evidenceDir);
}

static async Task<int> RunPerfAsync(string[] args)
{
    Dictionary<string, string> opts = ParseOptions(args);
    string root = Opt(opts, "data-root")
        ?? Environment.GetEnvironmentVariable("BENTAGO_DATA_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bg003-perf-" + Guid.NewGuid().ToString("N"));
    return await BentaGo.Tools.PerfRunner.RunAsync(root);
}

static async Task<int> RunPrintPerfAsync(string[] args)
{
    Dictionary<string, string> opts = ParseOptions(args);
    string root = Opt(opts, "data-root")
        ?? Path.Combine(Path.GetTempPath(), "bg007-perf-" + Guid.NewGuid().ToString("N"));
    string outPath = Opt(opts, "out") ?? "perf.tsv";
    string head = Opt(opts, "head") ?? Environment.GetEnvironmentVariable("BENTAGO_HEAD") ?? "unknown";
    return await BentaGo.Tools.PrintPerfRunner.RunAsync(root, outPath, head);
}

static int RunStatus()
{
    var paths = new DataPathProvider();
    paths.EnsureCreated();

    string dbPath = Path.Combine(paths.DataDir, "bentago.db");
    Console.WriteLine($"DATA_ROOT: {paths.Root}");
    Console.WriteLine($"DB_PATH:   {dbPath}");

    if (File.Exists(dbPath))
    {
        Console.WriteLine($"SCHEMA:    {ReadLatestMigrationId(dbPath) ?? "(no migrations)"}");
        Console.WriteLine($"SHA256:    {ComputeSha256(dbPath)}");
    }
    else
    {
        Console.WriteLine("SCHEMA:    (no database)");
        Console.WriteLine("SHA256:    (no database)");
    }

    return 0;
}

static async Task<int> RunMigrateAsync(string[] args)
{
    string root = ResolveDataRoot(args);
    ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
    var initializer = host.GetRequiredService<DbInitializer>();
    await initializer.EnsureCreatedOrMigrateAsync();

    string dbPath = Path.Combine(host.GetRequiredService<IDataPathProvider>().DataDir, "bentago.db");
    Console.WriteLine($"DATA_ROOT: {root}");
    Console.WriteLine($"SCHEMA:    {ReadLatestMigrationId(dbPath) ?? "(none)"}");
    return 0;
}

static async Task<int> RunBackupAsync(string[] args)
{
    string root = ResolveDataRoot(args);
    ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
    var backupService = host.GetRequiredService<IBackupService>();
    BackupReceipt receipt = await backupService.CreateAsync();
    Console.WriteLine($"BACKUP:       {receipt.BackupPath}");
    Console.WriteLine($"DB_VERSION:   {receipt.DatabaseVersion}");
    Console.WriteLine($"SHA256:       {receipt.Sha256}");
    Console.WriteLine($"CREATED_UTC:  {receipt.CreatedUtc:O}");
    return 0;
}

static async Task<int> RunRestoreAsync(string[] args)
{
    if (args.Length == 0 || args[0].StartsWith("--"))
    {
        Console.Error.WriteLine("usage: foundation restore <backup-path> [--data-root <dir>]");
        return 1;
    }

    string backupPath = args[0];
    if (!File.Exists(backupPath))
    {
        Console.Error.WriteLine($"ERROR: backup file not found: {backupPath}");
        return 1;
    }

    string root = ResolveDataRoot(args.Skip(1).ToArray());
    ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
    var restoreService = host.GetRequiredService<IRestoreService>();
    RestoreReceipt receipt = await restoreService.RestoreAsync(backupPath);
    Console.WriteLine($"RESTORED:        {receipt.RestoredPath}");
    Console.WriteLine($"PRE_RESTORE:     {receipt.PreRestoreCopyPath}");
    Console.WriteLine($"VERIFIED:        {receipt.Verified}");
    return 0;
}

static async Task<int> RunVerifyAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "bentago-verify-" + Guid.NewGuid().ToString("N"));

    try
    {
        ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);

        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();

        var seeder = host.GetRequiredService<IAdminSeeder>();
        await seeder.SeedAsync();

        var backupService = host.GetRequiredService<IBackupService>();
        BackupReceipt backup = await backupService.CreateAsync();

        var restoreService = host.GetRequiredService<IRestoreService>();
        RestoreReceipt restore = await restoreService.RestoreAsync(backup.BackupPath);

        string dbPath = Path.Combine(host.GetRequiredService<IDataPathProvider>().DataDir, "bentago.db");
        string sha256After = ComputeSha256(dbPath);
        bool adminPresent = await AdminSeedPresentAsync(dbPath);

        Console.WriteLine($"DB_VERSION:       {backup.DatabaseVersion}");
        Console.WriteLine($"BACKUP:           {backup.BackupPath}");
        Console.WriteLine($"SHA256_BEFORE:    {backup.Sha256}");
        Console.WriteLine($"SHA256_AFTER:     {sha256After}");
        Console.WriteLine($"ADMIN_PRESENT:    {adminPresent}");

        bool ok = adminPresent && string.Equals(backup.Sha256, sha256After, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"VERIFY:           {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"VERIFY:           FAIL ({ex.Message})");
        return 1;
    }
    finally
    {
        TryDeleteDirectory(root);
    }
}

static int UnknownCommand(string[] args)
{
    Console.Error.WriteLine($"ERROR: unknown command '{args[0]}'. Run 'BentaGo.Tools help' for usage.");
    return 1;
}

/// <summary>
/// Seeds an isolated root for the BG-006 sales demo recording: an open trip
/// with the catalog + stock the seller needs (mirrors tests/AppShell
/// SalesSeed so the recording state matches the lane state).
/// </summary>
static async Task<int> RunBg006LaneSeedAsync(string[] args)
{
    string root = ResolveDataRoot(args);

    ServiceProvider host = BentaGoHost.Build(development: true, dataRootOverride: root);
    await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
    await host.GetRequiredService<IAdminSeeder>().SeedAsync();

    var store = host.GetRequiredService<SqliteLedgerStore>();
    var ids = new ScenarioIdSource("appshell-sales-v1");

    TripId trip = new(ids.Next("trip"));
    ProductId product = new(ids.Next("product"));
    ProductId kopi = new(ids.Next("kopi"));
    LocationId route = new(ids.Next("route"));
    LocationId storeLoc = new(ids.Next("store"));
    UserId user = new(ids.Next("user"));
    PeddlerId peddler = new(ids.Next("peddler"));

    var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    store.RegisterTrip(new Trip(trip, peddler, route, when, user));

    var openId = new LoadId(ids.Next("open"));
    store.RegisterLoad(new StockLoad(openId, "P-OPEN", trip, Array.Empty<StockLoadLine>(), user, when));
    store.Bind();
    Result<AppendReceipt> open = store.Append(new[]
    {
        Moves.OpeningStock(0, when, user, new LoadRef(openId), product, storeLoc, Quantity.Of(100)),
        Moves.OpeningStock(1, when, user, new LoadRef(openId), kopi, storeLoc, Quantity.Of(100)),
    });
    if (!open.IsSuccess)
    {
        Console.Error.WriteLine($"SEED:           FAIL ({open.Errors.FirstOrDefault()?.Code ?? "opening stock failed"})");
        return 1;
    }

    var loadId = new LoadId(ids.Next("load"));
    Result<LoadDraft> load = LoadBuilder.Build(store.Context, new LoadCommand(
        loadId, "P-LOAD", trip, storeLoc,
        new[]
        {
            new LoadLine(product, "SARDINES", "Sardines", Quantity.Of(50), Money.FromPesos(30m)),
            new LoadLine(kopi, "KOPI", "Kopi", Quantity.Of(50), Money.FromPesos(30m)),
        },
        user, when));
    if (!load.IsSuccess)
    {
        Console.Error.WriteLine($"SEED:           FAIL ({load.Errors.FirstOrDefault()?.Code ?? "load build failed"})");
        return 1;
    }

    store.RegisterLoad(load.Value.Document);
    store.Bind();
    Result<AppendReceipt> loadAppend = store.Append(load.Value.Movements);
    if (!loadAppend.IsSuccess)
    {
        Console.Error.WriteLine($"SEED:           FAIL ({loadAppend.Errors.FirstOrDefault()?.Code ?? "load append failed"})");
        return 1;
    }

    await host.GetRequiredService<IUserAccountStore>().AddAsync(new User
    {
        Id = user,
        Username = "seller",
        PasswordHash = host.GetRequiredService<IPasswordHasher>().Hash("seller"),
        Role = Role.Peddler,
        PeddlerLink = peddler,
        CreatedBy = user,
        CreatedUtc = when,
    });

    var products = host.GetRequiredService<IProductStore>();
    await products.UpsertBySkuAsync(new Product(
        product, "SARDINES", "4801234567890", "Sardines", "canned", new Category("Canned"),
        "piece", Money.FromPesos(45m), null, null, false, null,
        SupportsPwdDiscount: true, SupportsSeniorDiscount: false));
    await products.UpsertBySkuAsync(new Product(
        kopi, "KOPI", "4801234567891", "Kopi", "coffee", new Category("Drinks"),
        "sachet", Money.FromPesos(10m), null, null, false, null,
        SupportsPwdDiscount: false, SupportsSeniorDiscount: false));

    Console.WriteLine($"SEED:           PASS");
    Console.WriteLine($"DATA_ROOT:      {root}");
    return 0;
}

static string ResolveDataRoot(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--data-root")
        {
            return args[i + 1];
        }
    }

    return Environment.GetEnvironmentVariable("BENTAGO_DATA_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "bentago-tools-" + Guid.NewGuid().ToString("N"));
}

static string? ReadLatestMigrationId(string dbPath)
{
    using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText =
        "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
    return command.ExecuteScalar() as string;
}

static async Task<bool> AdminSeedPresentAsync(string dbPath)
{
    using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    await connection.OpenAsync();

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(1) FROM \"AppSettings\" WHERE \"Key\" = 'seed.admin.username'";
    object? result = await command.ExecuteScalarAsync();
    return Convert.ToInt64(result) > 0;
}

static string ComputeSha256(string filePath)
{
    using var stream = File.OpenRead(filePath);
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
}

static void TryDeleteDirectory(string path)
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
        // Best-effort cleanup only.
    }
}
