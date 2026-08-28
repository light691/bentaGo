using BentaGo.Infrastructure.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Applies EF migrations on startup and surfaces schema identity for logs,
/// tools output, and backup receipts. Per C9/lane 9, an EXISTING database
/// with pending migrations is backed up FIRST (on a separate short-lived
/// connection, never on the store's connection — R2); a fresh root skips the
/// backup (nothing exists to preserve).
/// </summary>
public sealed class DbInitializer
{
    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly IBackupService _backup;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        IDbContextFactory<BentaGoDbContext> factory,
        IBackupService backup,
        ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _backup = backup;
        _logger = logger;
    }

    public async Task EnsureCreatedOrMigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        string? dbPath = DbFilePath(context.Database.GetDbConnection().ConnectionString);
        bool existedBefore = dbPath is not null && File.Exists(dbPath);
        IReadOnlyList<string> pending = await PendingMigrationsAsync(context.Database, cancellationToken);

        if (existedBefore && pending.Count > 0)
        {
            await _backup.CreateAsync(cancellationToken);
        }

        try
        {
            await context.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "database cannot be opened: " +
                $"the BentaGo database at '{context.Database.GetDbConnection().ConnectionString}' " +
                "is locked, corrupt, or otherwise unusable. See inner exception for details.", ex);
        }

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT sqlite_version()";
            string sqliteVersion = (versionCommand.ExecuteScalar() as string) ?? "unknown";

            using var userVersionCommand = connection.CreateCommand();
            userVersionCommand.CommandText = "PRAGMA user_version";
            long userVersion = Convert.ToInt64(userVersionCommand.ExecuteScalar());

            string? migrationId = (await context.Database
                .GetAppliedMigrationsAsync(cancellationToken))
                .LastOrDefault();

            _logger.LogInformation(
                "database schema: sqlite={SqliteVersion} user_version={UserVersion} migration={MigrationId}",
                sqliteVersion, userVersion, migrationId ?? "(none)");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<IReadOnlyList<string>> PendingMigrationsAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        string[] known = database.GetMigrations().ToArray();
        if (known.Length == 0)
        {
            return Array.Empty<string>();
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        var connection = database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        try
        {
            bool historyTableExists;
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText =
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
                historyTableExists = Convert.ToInt64(probe.ExecuteScalar()) == 1;
            }

            if (historyTableExists)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    applied.Add(reader.GetString(0));
                }
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        return known.Where(id => !applied.Contains(id)).ToArray();
    }

    private static string? DbFilePath(string connectionString)
    {
        foreach (string part in connectionString.Split(';'))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Data Source=".Length..].Trim();
            }
        }

        return null;
    }
}
