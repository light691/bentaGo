using System.IO;
using System.Security.Cryptography;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Foundation;

public sealed class BootstrapTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    private string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-tests-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            TryDeleteDirectory(root);
        }
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

    private static string DbPath(string root) => Path.Combine(root, "data", "bentago.db");

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    [Fact]
    public void DataPaths_OverrideRootAndCreateDirectories()
    {
        string root = NewTempRoot();

        var paths = new DataPathProvider(root);
        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "data"), paths.DataDir);
        Assert.Equal(Path.Combine(root, "backups"), paths.BackupsDir);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDir);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.DataDir));
        Assert.True(Directory.Exists(paths.BackupsDir));
        Assert.True(Directory.Exists(paths.LogsDir));
    }

    [Fact]
    public async Task FirstMigration_CreatesDatabaseWithAppSettingsTable()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();

        await initializer.EnsureCreatedOrMigrateAsync();

        string dbPath = DbPath(root);
        Assert.True(File.Exists(dbPath), "database file should exist after migration");

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'AppSettings'";
        Assert.Equal(1L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));

        using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText =
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        Assert.Equal(1L, Convert.ToInt64(await historyCommand.ExecuteScalarAsync()));

        await connection.CloseAsync();
    }

    [Fact]
    public async Task RepeatMigration_ConvergesWithoutError()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await initializer.EnsureCreatedOrMigrateAsync();

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(3, applied.Length); // InitialCreate + BG003_01 + BG003_02
        Assert.EndsWith("BG003_02_AddCheckoutIdempotency", applied[^1]);
    }

    [Fact]
    public async Task Seed_IsIdempotentAndHashVerifies()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var hasher = host.GetRequiredService<IPasswordHasher>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();
        await seeder.SeedAsync();

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();

        Assert.Equal(1, await context.AppSettings.CountAsync(s => s.Key == "seed.admin.username"));

        string? storedHash = await context.AppSettings
            .Where(s => s.Key == "seed.admin.passwordHash")
            .Select(s => s.Value)
            .SingleOrDefaultAsync();

        Assert.False(string.IsNullOrEmpty(storedHash), "password hash should be stored");
        Assert.True(hasher.Verify("admin", storedHash!), "stored hash must verify against 'admin'");
    }

    [Fact]
    public async Task Backup_QuickCheckOkAndShaMatchesReceipt()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var backupService = host.GetRequiredService<IBackupService>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();

        BackupReceipt receipt = await backupService.CreateAsync();

        Assert.True(File.Exists(receipt.BackupPath), "backup file should exist");
        Assert.Equal(ComputeSha256(receipt.BackupPath), receipt.Sha256);

        await using var connection = new SqliteConnection($"Data Source={receipt.BackupPath};Mode=ReadOnly");
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        Assert.Equal("ok", (await command.ExecuteScalarAsync()) as string);

        await connection.CloseAsync();
    }

    [Fact]
    public async Task Restore_YieldsWorkingDatabaseAndPreservesPreRestoreCopy()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var backupService = host.GetRequiredService<IBackupService>();
        var restoreService = host.GetRequiredService<IRestoreService>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();

        BackupReceipt backup = await backupService.CreateAsync();
        RestoreReceipt restored = await restoreService.RestoreAsync(backup.BackupPath);

        Assert.True(restored.Verified);
        Assert.True(File.Exists(restored.PreRestoreCopyPath), "pre-restore copy should be preserved");
        Assert.True(File.Exists(DbPath(root)), "active database should exist after restore");
        Assert.Equal(backup.Sha256, ComputeSha256(DbPath(root)));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await context.AppSettings.CountAsync(s => s.Key == "seed.admin.username"));
    }

    [Fact]
    public async Task Restore_CorruptFileRefusedAndActiveDatabaseUnchanged()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var restoreService = host.GetRequiredService<IRestoreService>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();

        string corruptBackup = Path.Combine(root, "backups", "corrupt.db");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptBackup)!);
        File.WriteAllBytes(corruptBackup, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 });

        string dbBefore = ComputeSha256(DbPath(root));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => restoreService.RestoreAsync(corruptBackup));

        Assert.Equal(dbBefore, ComputeSha256(DbPath(root)));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await context.AppSettings.CountAsync(s => s.Key == "seed.admin.username"));
    }

    [Fact]
    public async Task Restore_WrongSchemaRefusedAndActiveDatabaseUnchanged()
    {
        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var restoreService = host.GetRequiredService<IRestoreService>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();

        string foreignBackup = Path.Combine(root, "backups", "foreign-schema.db");
        Directory.CreateDirectory(Path.GetDirectoryName(foreignBackup)!);

        await using (var writer = new SqliteConnection($"Data Source={foreignBackup};Pooling=False"))
        {
            await writer.OpenAsync();

            using var create = writer.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL);
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20200101000000_BogusSchema', '10.0.0');
                """;
            await create.ExecuteNonQueryAsync();

            await writer.CloseAsync();
        }

        string dbBefore = ComputeSha256(DbPath(root));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => restoreService.RestoreAsync(foreignBackup));

        Assert.Equal(dbBefore, ComputeSha256(DbPath(root)));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await context.AppSettings.CountAsync(s => s.Key == "seed.admin.username"));
    }

    [Fact]
    public async Task Restore_SeedMissingRefusedAndActiveDatabaseUnchanged()
    {
        // A schema-valid backup that never got seeded must be refused by the
        // post-copy seed verification, and the active database must be intact.
        string seedlessRoot = NewTempRoot();
        ServiceProvider seedlessHost = BentaGoHost.BuildForTests(seedlessRoot);
        await seedlessHost.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        BackupReceipt seedlessBackup = await seedlessHost
            .GetRequiredService<IBackupService>().CreateAsync();

        string root = NewTempRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        var restoreService = host.GetRequiredService<IRestoreService>();

        await initializer.EnsureCreatedOrMigrateAsync();
        await seeder.SeedAsync();

        string dbBefore = ComputeSha256(DbPath(root));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => restoreService.RestoreAsync(seedlessBackup.BackupPath));
        Assert.Contains("admin seed row", ex.Message);

        Assert.Equal(dbBefore, ComputeSha256(DbPath(root)));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await context.AppSettings.CountAsync(s => s.Key == "seed.admin.username"));
    }
}
