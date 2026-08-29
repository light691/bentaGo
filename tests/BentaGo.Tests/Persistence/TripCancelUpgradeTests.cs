using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// BG-005c 3a-style migration test: the prior-schema fixture (migration 01,
/// WITH a seeded ledger) upgrades to head; the BG005c_01 migration rebuilds
/// the Movements table (SQLite cannot ALTER a CHECK) and every existing row
/// and total must survive verbatim, and the new 'TripCancel' movement kind is
/// accepted afterwards — the upgrade is the proof that existing roots keep
/// working (upgrade-consistency).
/// </summary>
public sealed class TripCancelUpgradeTests : IDisposable
{
    private readonly List<string> _roots = new();

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

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-tripcancel-mig-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static string DbPath(string root) => Path.Combine(root, "data", "bentago.db");

    [Fact]
    public async Task PriorSchemaFixture_UpgradesToHead_RowsPreserved_AndTripCancelAccepted()
    {
        string root = NewRoot();
        string dbPath = await LedgerSchemaFixture.BuildAsync(root);

        long movementsBefore = await CountAsync(dbPath, "Movements");
        long loadRowsBefore = await CountAsync(dbPath, "StockLoads");
        long saleRowsBefore = await CountAsync(dbPath, "Sales");
        long auditRowsBefore = await CountAsync(dbPath, "AuditEvents");
        long? stockTotalBefore = await SumAsync(dbPath, "quantity");
        long? fundsTotalBefore = await SumAsync(dbPath, "amount_centavos");

        // Upgrade to head: BG005c_01 rebuilds Movements with the TripCancel kind.
        ServiceProvider host = BentaGoHost.BuildForTests(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        Assert.Equal(6, await CountAsync(dbPath, "__EFMigrationsHistory"));
        Assert.Equal(movementsBefore, await CountAsync(dbPath, "Movements"));
        Assert.Equal(loadRowsBefore, await CountAsync(dbPath, "StockLoads"));
        Assert.Equal(saleRowsBefore, await CountAsync(dbPath, "Sales"));
        Assert.Equal(auditRowsBefore, await CountAsync(dbPath, "AuditEvents"));
        Assert.Equal(stockTotalBefore, await SumAsync(dbPath, "quantity"));
        Assert.Equal(fundsTotalBefore, await SumAsync(dbPath, "amount_centavos"));

        // The upgraded root accepts a TripCancel movement through the real store.
        var store = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        var trip = new TripId(Guid.NewGuid());
        var admin = new UserId(Guid.NewGuid());
        store.RegisterTrip(new Trip(trip, new PeddlerId(Guid.NewGuid()), new LocationId(Guid.NewGuid()),
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero), admin));
        store.Bind();

        Result<AppendReceipt> appended = store.Append(new[]
        {
            Moves.TripCancel(0, new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), admin,
                new CancelRef(trip), trip, "post-upgrade cancel"),
        });
        Assert.True(appended.IsSuccess, appended.Errors.FirstOrDefault()?.Code);
        Assert.Equal(TripStatus.Cancelled, store.State.Trip(trip).Status);

        // The durable projection reflects the cancellation.
        await using (var checkConnection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
        {
            await checkConnection.OpenAsync();
            using var command = checkConnection.CreateCommand();
            command.CommandText = "SELECT \"status\" FROM \"TripStates\" WHERE \"trip_id\" = $trip";
            command.Parameters.AddWithValue("$trip", trip.Value.ToString("D"));
            Assert.Equal("Cancelled", await command.ExecuteScalarAsync() as string);
            await checkConnection.CloseAsync();
        }

        // A fresh fold over the same file still sees the cancellation.
        var reopened = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        Assert.Equal(TripStatus.Cancelled, reopened.State.Trip(trip).Status);
        reopened.Close();
        store.Close();
    }

    private static async Task<long> CountAsync(string dbPath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
        long count = Convert.ToInt64(await command.ExecuteScalarAsync());
        await connection.CloseAsync();
        return count;
    }

    private static async Task<long?> SumAsync(string dbPath, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SUM(\"{column}\") FROM \"Movements\"";
        object? result = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }
}
