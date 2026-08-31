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
        "ledger" when commandLine.Length > 1 => await RunLedgerAsync(commandLine.Skip(1).ToArray()),
        "lane8-crash-worker" => await RunLane8CrashWorkerAsync(commandLine.Skip(1).ToArray()),
        "perf" when commandLine.Length > 1 && commandLine[1] == "bg-003" => await RunPerfAsync(commandLine.Skip(2).ToArray()),
        "perf" when commandLine.Length > 1 && commandLine[1] == "bg-007" => await RunPrintPerfAsync(commandLine.Skip(2).ToArray()),
        "perf" when commandLine.Length > 1 && commandLine[1] == "bg-008" => await RunBg008PerfAsync(commandLine.Skip(2).ToArray()),
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
          ledger day-open|checkout|void|return|adjustment|count|close|reopen|rebuild [options]
                                         Run one business operation against the SQLite ledger. Common
                                         options: --data-root <dir>, --by <user-id>, --when <utc-iso>.
                                         day-open: --float <pesos> (default 0). close: --counted <pesos>
                                         [--notes <text>]; reopen: --reason <text> [--approved-by <id>].
                                         checkout extra options: --key <checkout-key> --receipt <n>
                                           --product <id> --qty <n> --price <pesos> --method <Cash|Gcash|Maya|BankTransfer|Credit>
                                           --fail-before-commit (L3 seam) --crash-after-write (L8 seam)
          lane8-crash-worker             Lane-8 crash child: sells the last unit in a transaction delayed
                                         before commit; parent kills it by PID. Options: --data-root <dir>
          perf bg-003                    BG-003 perf harness: baselines first, then P95 checkout and
                                         rebuild over a 10k-product / 60k-movement seeded file.
                                          Options: --data-root <dir>
          perf bg-008                    BG-008 perf probe: baselines (bg-003 aggregation, bg-006
                                         history load) first, then close-calc and report-render P95
                                         over a 10,000-sale seeded file.
                                           Options: --data-root <dir>, --out <tsv>, --head <sha>
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
                                           demo recording: open day with 50 Sardines/Kopi in the
                                           warehouse, catalog products, and the seller user
                                           (mirrors the tests/AppShell SalesSeed scenario).
        """);
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
        Console.Error.WriteLine("usage: ledger day-open|checkout|void|return|adjustment|count|close|reopen|rebuild [options]");
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
            case "day-open":
                {
                    // Opens the trading day (movement-less DayOpen document).
                    DayKey day = DayKey.FromUtc(when);
                    store.Bind();
                    Result<DayOpenDraft> draft = DayBuilder.Open(store.Context, new DayOpenCommand(
                        new DayId(Guid.NewGuid()),
                        day,
                        decimal.TryParse(Opt(opts, "float"), out decimal floatPesos) ? Money.FromPesos(floatPesos).Centavos : 0,
                        by, when));
                    if (draft.IsFailure)
                    {
                        Console.Error.WriteLine($"DAY-OPEN: FAIL ({draft.Errors[0].Code}: {draft.Errors[0].Message})");
                        return 1;
                    }

                    store.RegisterDayOpen(draft.Value.Document);
                    store.RecordAudit(new AuditEvent(
                        -1, by, when, "DAY.OPEN", $"day {day} opened", null, Terminal: Environment.MachineName));
                    Console.WriteLine($"DAY-OPEN:  {day} float {draft.Value.Document.OpeningFloatCentavos}");
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
                        DayKey.FromUtc(when),
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
                        DayKey.FromUtc(when),
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
                    Result<CloseOutcome> result = host.GetRequiredService<ICloseService>().Close(new DayCloseCommand(
                        closeId,
                        DayKey.FromUtc(when),
                        Money.FromPesos(decimal.Parse(RequireOpt(opts, "counted"))).Centavos,
                        Opt(opts, "notes"),
                        by, when));
                    return PrintSimple(result, "CLOSE", $"day {DayKey.FromUtc(when)}");
                }

            case "reopen":
                {
                    UserId approver = opts.ContainsKey("approved-by") ? new UserId(OptGuid(opts, "approved-by", "approver user id")) : by;
                    Result<AppendReceipt> result = host.GetRequiredService<IReopenService>().Reopen(new DayReopenCommand(
                        new DayId(Guid.NewGuid()), DayKey.FromUtc(when), RequireOpt(opts, "reason"), approver, by, when));
                    if (result.IsFailure)
                    {
                        Console.Error.WriteLine($"REOPEN: FAIL ({string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))})");
                        return 1;
                    }

                    Console.WriteLine($"REOPEN:    ok seq {result.Value.FirstSequence}-{result.Value.LastSequence}");
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

static async Task<int> RunBg008PerfAsync(string[] args)
{
    Dictionary<string, string> opts = ParseOptions(args);
    string root = Opt(opts, "data-root")
        ?? Path.Combine(Path.GetTempPath(), "bg008-perf-" + Guid.NewGuid().ToString("N"));
    string outPath = Opt(opts, "out") ?? "perf.tsv";
    string head = Opt(opts, "head") ?? Environment.GetEnvironmentVariable("BENTAGO_HEAD") ?? "unknown";
    return await BentaGo.Tools.Bg008PerfRunner.RunAsync(root, outPath, head);
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
/// Seeds an isolated root for the BG-006 sales demo recording: an open day
/// with the catalog + warehouse stock the seller needs (mirrors tests/AppShell
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

    ProductId product = new(ids.Next("product"));
    ProductId kopi = new(ids.Next("kopi"));
    UserId user = new(ids.Next("user"));

    var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    DayKey day = DayKey.FromUtc(when);
    LocationId warehouse = StoreLocations.Warehouse;

    store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), day, 0, user, when));
    store.Bind();

    var goodsId = new GoodsId(ids.Next("goods"));
    store.Bind();
    Result<AppendReceipt> received = store.Append(new[]
    {
        Moves.GoodsReceived(0, when, user, new GoodsRef(goodsId), product, warehouse, Quantity.Of(100)),
        Moves.GoodsReceived(1, when, user, new GoodsRef(goodsId), kopi, warehouse, Quantity.Of(100)),
    });
    if (!received.IsSuccess)
    {
        Console.Error.WriteLine($"SEED:           FAIL ({received.Errors.FirstOrDefault()?.Code ?? "goods receipt failed"})");
        return 1;
    }

    await host.GetRequiredService<IUserAccountStore>().AddAsync(new User
    {
        Id = user,
        Username = "seller",
        PasswordHash = host.GetRequiredService<IPasswordHasher>().Hash("seller"),
        Role = Role.Supervisor,
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
