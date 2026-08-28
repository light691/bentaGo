using System.IO;
using System.Security.Cryptography;
using BentaGo.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace BentaGo.Infrastructure.Backup;

/// <summary>
/// Creates an online SQLite backup of the active database into the backups
/// directory, then verifies the copy and hashes it.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IDataPathProvider _paths;

    public BackupService(IDataPathProvider paths)
    {
        _paths = paths;
    }

    public async Task<BackupReceipt> CreateAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        string dbPath = Path.Combine(_paths.DataDir, "bentago.db");
        if (!File.Exists(dbPath))
        {
            throw new InvalidOperationException($"database cannot be backed up: no database at '{dbPath}'");
        }

        DateTime createdUtc = DateTime.UtcNow;
        string backupPath = Path.Combine(
            _paths.BackupsDir,
            $"bentago-{createdUtc:yyyyMMddHHmmssfff}.db");

        string databaseVersion = await ReadMigrationIdAsync(dbPath, cancellationToken);

        await using (var source = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Pooling=False"))
        await using (var destination = new SqliteConnection($"Data Source={backupPath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
            await destination.CloseAsync();
        }

        await VerifyQuickCheckAsync(backupPath, cancellationToken);

        string sha256 = ComputeSha256(backupPath);
        return new BackupReceipt(backupPath, databaseVersion, sha256, createdUtc);
    }

    private async Task<string> ReadMigrationIdAsync(string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result as string ?? "unknown";
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static async Task VerifyQuickCheckAsync(string dbPath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"database integrity check failed on '{dbPath}': quick_check returned '{result}'");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"database integrity check failed on '{dbPath}': the file is not a valid SQLite database.", ex);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    internal static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
