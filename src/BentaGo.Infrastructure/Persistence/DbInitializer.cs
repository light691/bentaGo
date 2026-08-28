using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Applies EF migrations on startup and surfaces schema identity for logs,
/// tools output, and backup receipts.
/// </summary>
public sealed class DbInitializer
{
    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(IDbContextFactory<BentaGoDbContext> factory, ILogger<DbInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task EnsureCreatedOrMigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

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
}
