using System.IO;
using System.Security.Cryptography;
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
        "foundation" when commandLine.Length > 1 && commandLine[1] == "migrate" => await RunMigrateAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "backup" => await RunBackupAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "restore" => await RunRestoreAsync(commandLine.Skip(2).ToArray()),
        "foundation" when commandLine.Length > 1 && commandLine[1] == "verify" => await RunVerifyAsync(),
        _ => UnknownCommand(commandLine),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        BentaGo.Tools - foundation CLI for the BentaGo host.

        Commands:
          status                         Print data root, database path, schema version, database sha256.
          foundation migrate             Migrate the database schema to the latest version.
                                          Options: --data-root <dir> (default: BENTAGO_DATA_ROOT or a fresh temp dir)
          foundation backup              Create a verified backup of the active database.
                                          Options: --data-root <dir>
          foundation restore <path>      Restore the active database from <path> (validated, refused on mismatch).
                                          Options: --data-root <dir>
          foundation verify              End-to-end proof: migrate, seed, backup, restore, verify.
                                          Runs entirely under a fresh temp data root and exits 0 only if all checks pass.
          help                           Show this text.
        """);
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
