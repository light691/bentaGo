using System.IO;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U6 — MigrationTests (docs/05 lane 9): a fresh root migrates to the head
/// schema with the full ledger shape; a code-built fixture at migration 01
/// (prior schema WITH a seeded ledger) upgrades to head with every row and
/// total preserved.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly List<string> _roots = new();

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-mig-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static string DbPath(string root) => Path.Combine(root, "data", "bentago.db");

    [Fact]
    public async Task FreshRoot_MigratesToHead_WithFullLedgerSchema()
    {
        string root = NewRoot();
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(4, applied.Length); // InitialCreate + BG003_01 + BG003_02 + BG005_01
        Assert.EndsWith("BG005_01_AddIdentityAndAudit", applied[^1]);

        await using var connection = new SqliteConnection($"Data Source={DbPath(root)};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "Movements"));
        Assert.True(await TableExistsAsync(connection, "IdempotencyKeys"));
        Assert.True(await TableExistsAsync(connection, "Trips"));
        Assert.True(await TableExistsAsync(connection, "Sales"));
        Assert.True(await TableExistsAsync(connection, "SaleLines"));
        Assert.True(await TableExistsAsync(connection, "PaymentLines"));
        Assert.True(await TableExistsAsync(connection, "StockLoads"));
        Assert.True(await TableExistsAsync(connection, "LedgerHead"));
        Assert.True(await TableExistsAsync(connection, "CellBalances"));
        Assert.True(await TableExistsAsync(connection, "FundBalances"));
        Assert.True(await TableExistsAsync(connection, "ObservedQuantities"));
        Assert.True(await TableExistsAsync(connection, "TripStates"));
        Assert.True(await TableExistsAsync(connection, "CellBreakdown"));
        Assert.True(await TableExistsAsync(connection, "AuditEvents"));
        Assert.True(await TableExistsAsync(connection, "Users"));
        Assert.True(await TableExistsAsync(connection, "Peddlers"));

        // Append-only triggers on Movements.
        Assert.True(await TriggerExistsAsync(connection, "Movements_NoUpdate"));
        Assert.True(await TriggerExistsAsync(connection, "Movements_NoDelete"));

        // A second migrate converges.
        await initializer.EnsureCreatedOrMigrateAsync();
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());

        await connection.CloseAsync();
    }

    [Fact]
    public async Task FixtureAtMigration01_UpgradesToHead_RowsAndTotalsPreserved()
    {
        string root = NewRoot();
        string dbPath = await LedgerSchemaFixture.BuildAsync(root);
        Assert.EndsWith("bentago.db", dbPath);

        // Pre-upgrade shape: migration history stops at BG003_01; no idempotency table yet.
        await using (var before = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
        {
            await before.OpenAsync();
            Assert.True(await TableExistsAsync(before, "Movements"));
            Assert.False(await TableExistsAsync(before, "IdempotencyKeys"));
            Assert.Equal(2, await CountRowsAsync(before, "__EFMigrationsHistory"));
            await before.CloseAsync();
        }

        long movementsBefore = await CountRowsAsync(dbPath, "Movements");
        long loadRowsBefore = await CountRowsAsync(dbPath, "StockLoads");
        long saleRowsBefore = await CountRowsAsync(dbPath, "Sales");
        long auditRowsBefore = await CountRowsAsync(dbPath, "AuditEvents");
        long? totalStockBefore = await SumColumnAsync(dbPath, "Movements", "quantity");
        long? totalFundsBefore = await SumColumnAsync(dbPath, "Movements", "amount_centavos");

        // Upgrade to head.
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();

        Assert.Equal(4, await CountRowsAsync(dbPath, "__EFMigrationsHistory"));
        Assert.True(await TableExistsAsync(dbPath, "IdempotencyKeys"));
        Assert.True(await TableExistsAsync(dbPath, "Users"));
        Assert.True(await TableExistsAsync(dbPath, "Peddlers"));

        // Rows and totals survived the upgrade verbatim.
        Assert.Equal(movementsBefore, await CountRowsAsync(dbPath, "Movements"));
        Assert.Equal(loadRowsBefore, await CountRowsAsync(dbPath, "StockLoads"));
        Assert.Equal(saleRowsBefore, await CountRowsAsync(dbPath, "Sales"));
        Assert.Equal(auditRowsBefore, await CountRowsAsync(dbPath, "AuditEvents"));
        Assert.Equal(totalStockBefore, await SumColumnAsync(dbPath, "Movements", "quantity"));
        Assert.Equal(totalFundsBefore, await SumColumnAsync(dbPath, "Movements", "amount_centavos"));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.EndsWith("BG005_01_AddIdentityAndAudit", applied[^1]);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> TableExistsAsync(string dbPath, string name)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        bool exists = await TableExistsAsync(connection, name);
        await connection.CloseAsync();
        return exists;
    }

    private static async Task<bool> TriggerExistsAsync(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'trigger' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<long> CountRowsAsync(string dbPath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        long count = await CountRowsAsync(connection, table);
        await connection.CloseAsync();
        return count;
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long?> SumColumnAsync(string dbPath, string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SUM(\"{column}\") FROM \"{table}\"";
        object? result = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }
}
