using System.IO;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U6 — MigrationTests (docs/05 lane 9), retargeted to the BG-P1 schema
/// reset (P1c): a fresh root migrates through the ONE baseline migration to
/// the full day-era ledger shape, and the code-built seeded fixture
/// converges to head with every row and total preserved.
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
        Assert.Single(applied); // exactly ONE migration: the BG-P1 baseline
        Assert.EndsWith("Baseline", applied[^1]);

        await using var connection = new SqliteConnection($"Data Source={DbPath(root)};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "Movements"));
        Assert.True(await TableExistsAsync(connection, "IdempotencyKeys"));
        Assert.True(await TableExistsAsync(connection, "Sales"));
        Assert.True(await TableExistsAsync(connection, "SaleLines"));
        Assert.True(await TableExistsAsync(connection, "PaymentLines"));
        Assert.True(await TableExistsAsync(connection, "LedgerHead"));
        Assert.True(await TableExistsAsync(connection, "CellBalances"));
        Assert.True(await TableExistsAsync(connection, "FundBalances"));
        Assert.True(await TableExistsAsync(connection, "ObservedQuantities"));
        Assert.True(await TableExistsAsync(connection, "AuditEvents"));
        Assert.True(await TableExistsAsync(connection, "Users"));
        Assert.True(await TableExistsAsync(connection, "Products")); // BG-005b catalog
        Assert.True(await TableExistsAsync(connection, "PrintJobs")); // BG-007 print jobs

        // Day-era documents (BG-P1): day lifecycle + goods receipts + fund
        // entries, plus the BIR serial counter (docs/decisions/0003).
        Assert.True(await TableExistsAsync(connection, "DayOpens"));
        Assert.True(await TableExistsAsync(connection, "DayCloses"));
        Assert.True(await TableExistsAsync(connection, "DayReopens"));
        Assert.True(await TableExistsAsync(connection, "GoodsReceipts"));
        Assert.True(await TableExistsAsync(connection, "GoodsReceiptLines"));
        Assert.True(await TableExistsAsync(connection, "FundEntries"));
        Assert.True(await TableExistsAsync(connection, "ReceiptSerials"));

        // The schema reset killed every route-selling table (the pivot,
        // docs/decisions/0002): the surviving table set is exactly the
        // day-era one — no pre-pivot registry survives.
        Assert.False(await TableExistsAsync(connection, "Trips"));
        Assert.False(await TableExistsAsync(connection, "StockLoads"));
        Assert.False(await TableExistsAsync(connection, "TripStates"));
        Assert.False(await TableExistsAsync(connection, "CellBreakdown"));
        Assert.False(await TableExistsAsync(connection, "DailyCloses"));
        HashSet<string> tables = await TableNamesAsync(connection);
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase)
        {
            "AppSettings", "PrintJobs", "Products", "Users",
            "Movements", "Sales", "SaleLines", "PaymentLines",
            "SaleVoids", "Returns", "ReturnLines", "Adjustments",
            "PhysicalCounts", "CountLines", "DayOpens", "DayCloses",
            "DayReopens", "GoodsReceipts", "GoodsReceiptLines", "FundEntries",
            "ReceiptSerials", "AuditEvents", "LedgerHead", "CellBalances",
            "FundBalances", "ObservedQuantities", "IdempotencyKeys",
            "__EFMigrationsHistory", "__EFMigrationsLock",
        };
        foreach (string table in tables)
        {
            Assert.True(expected.Contains(table), $"unexpected table '{table}': the schema reset left pre-pivot objects behind");
        }

        // Append-only triggers on Movements.
        Assert.True(await TriggerExistsAsync(connection, "Movements_NoUpdate"));
        Assert.True(await TriggerExistsAsync(connection, "Movements_NoDelete"));

        // BIR + day-keyed indexes; caller-surface performance indexes.
        Assert.True(await IndexExistsAsync(connection, "IX_Sales_SerialNumber"));
        Assert.True(await IndexExistsAsync(connection, "IX_Sales_DayKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_DayOpens_DayKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_Products_Name_NOCASE"));
        Assert.True(await IndexExistsAsync(connection, "IX_PaymentLines_Reference"));

        // A second migrate converges.
        await initializer.EnsureCreatedOrMigrateAsync();
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());

        await connection.CloseAsync();
    }

    [Fact]
    public async Task FixtureAtBaseline_ConvergesToHead_RowsAndTotalsPreserved()
    {
        string root = NewRoot();
        string dbPath = await LedgerSchemaFixture.BuildAsync(root);
        Assert.EndsWith("bentago.db", dbPath);

        // The fixture is already AT the one baseline: seeded mini-day present,
        // history holds exactly the baseline row.
        await using (var before = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
        {
            await before.OpenAsync();
            Assert.True(await TableExistsAsync(before, "Movements"));
            Assert.True(await TableExistsAsync(before, "IdempotencyKeys"));
            Assert.Equal(1, await CountRowsAsync(before, "__EFMigrationsHistory"));
            await before.CloseAsync();
        }

        long movementsBefore = await CountRowsAsync(dbPath, "Movements");
        long saleRowsBefore = await CountRowsAsync(dbPath, "Sales");
        long auditRowsBefore = await CountRowsAsync(dbPath, "AuditEvents");
        long? totalStockBefore = await SumColumnAsync(dbPath, "Movements", "quantity");
        long? totalFundsBefore = await SumColumnAsync(dbPath, "Movements", "amount_centavos");

        // A migrate pass converges to head (no-op) without touching data.
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();

        Assert.Equal(1, await CountRowsAsync(dbPath, "__EFMigrationsHistory"));
        Assert.True(await TableExistsAsync(dbPath, "Users"));
        Assert.True(await TableExistsAsync(dbPath, "Products")); // BG-005b catalog

        // Rows and totals survived verbatim.
        Assert.Equal(movementsBefore, await CountRowsAsync(dbPath, "Movements"));
        Assert.Equal(saleRowsBefore, await CountRowsAsync(dbPath, "Sales"));
        Assert.Equal(auditRowsBefore, await CountRowsAsync(dbPath, "AuditEvents"));
        Assert.Equal(totalStockBefore, await SumColumnAsync(dbPath, "Movements", "quantity"));
        Assert.Equal(totalFundsBefore, await SumColumnAsync(dbPath, "Movements", "amount_centavos"));

        await using var context = await host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>()
            .CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(applied); // head = the BG-P1 baseline
        Assert.EndsWith("Baseline", applied[^1]);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<HashSet<string>> TableNamesAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = $name";
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
