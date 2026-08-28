using System.IO;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Backup;

/// <summary>
/// Restores the active database from a backup file. Refuses to touch the
/// active database until the backup is proven valid and schema-compatible;
/// rolls the rename back if anything fails mid-restore.
/// </summary>
public sealed class RestoreService : IRestoreService
{
    private readonly IDataPathProvider _paths;
    private readonly IDbContextFactory<BentaGoDbContext> _factory;

    public RestoreService(IDataPathProvider paths, IDbContextFactory<BentaGoDbContext> factory)
    {
        _paths = paths;
        _factory = factory;
    }

    public async Task<RestoreReceipt> RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        string dataDir = _paths.DataDir;
        string dbPath = Path.Combine(dataDir, "bentago.db");
        string preRestorePath = Path.Combine(
            dataDir,
            $"bentago.pre-restore-{DateTime.UtcNow:yyyyMMddHHmmss}.db");

        if (!File.Exists(dbPath))
        {
            throw new InvalidOperationException($"restore refused: active database not found at '{dbPath}'");
        }

        // (a) Validate the backup without touching the active database.
        await BackupService.VerifyQuickCheckAsync(backupPath, cancellationToken);

        string backupVersion = await ReadMigrationIdAsync(backupPath, cancellationToken);
        string currentVersion = await ReadMigrationIdAsync(dbPath, cancellationToken);

        if (!string.Equals(backupVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"restore refused: schema mismatch — backup migration '{backupVersion}' " +
                $"does not match active database migration '{currentVersion}'; active database left unchanged");
        }

        // (b) Move the active database (and any stale journal companions)
        // aside, then put the backup in place. The whole window is inside
        // the rollback try: a hard failure at any point restores the
        // pre-restore files.
        string walPath = dbPath + "-wal";
        string shmPath = dbPath + "-shm";
        string preRestoreWalPath = preRestorePath + "-wal";
        string preRestoreShmPath = preRestorePath + "-shm";

        try
        {
            if (File.Exists(walPath))
            {
                File.Move(walPath, preRestoreWalPath);
            }

            if (File.Exists(shmPath))
            {
                File.Move(shmPath, preRestoreShmPath);
            }

            File.Move(dbPath, preRestorePath);

            // (c) Copy the backup into place.
            File.Copy(backupPath, dbPath);

            // (d) Verify the restored database still carries the admin seed row.
            await VerifyAdminSeedAsync(cancellationToken);
        }
        catch
        {
            // (e) Roll everything back and rethrow.
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }

            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }

            if (File.Exists(preRestorePath))
            {
                File.Move(preRestorePath, dbPath);
            }

            if (File.Exists(preRestoreWalPath) && !File.Exists(walPath))
            {
                File.Move(preRestoreWalPath, walPath);
            }

            if (File.Exists(preRestoreShmPath) && !File.Exists(shmPath))
            {
                File.Move(preRestoreShmPath, shmPath);
            }

            throw;
        }

        return new RestoreReceipt(backupPath, dbPath, preRestorePath, Verified: true);
    }

    private async Task VerifyAdminSeedAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        bool adminRowPresent = await context.AppSettings
            .AnyAsync(s => s.Key == "seed.admin.username", cancellationToken);

        if (!adminRowPresent)
        {
            throw new InvalidOperationException(
                "restored database failed verification: admin seed row ('seed.admin.username') is missing");
        }
    }

    private static async Task<string> ReadMigrationIdAsync(string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";

            var result = await command.ExecuteScalarAsync(cancellationToken);

            if (result is null)
            {
                throw new InvalidOperationException($"no EF migrations history found in '{dbPath}'");
            }

            return (string)result;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
