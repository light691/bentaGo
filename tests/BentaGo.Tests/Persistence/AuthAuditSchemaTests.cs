using System.IO;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// BG-005a AuditEvents schema hardening: identity PRIMARY KEY on Sequence,
/// the Terminal/Reason columns, and row-preserving behavior on the upgrade
/// path (the ledger's -1 placeholder rows are renumbered to real identities).
/// RecordAudit returns the persisted identity, and the DB row carries it.
/// </summary>
public sealed class AuthAuditSchemaTests : PersistenceTestBase
{
    private static readonly DateTimeOffset When = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshRoot_AuditEvents_HasIdentityPkOnSequence_AndTerminalReasonColumns()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var columns = await TableInfoAsync(DbPath(root), "AuditEvents");
        Assert.True(columns.TryGetValue("sequence", out int sequencePk), "sequence column missing");
        Assert.Equal(1, sequencePk); // the PK
        Assert.Contains("terminal", columns.Keys);
        Assert.Contains("reason", columns.Keys);
        Assert.Equal(0, columns["terminal"]); // not part of the PK
    }

    [Fact]
    public async Task RecordAudit_ReturnsPersistedIdentitySequence_DistinctPerRow()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        long one = store.RecordAudit(new AuditEvent(-1, UserId.Anonymous, When, "AUTH.TEST", "one", Ref: null));
        long two = store.RecordAudit(new AuditEvent(-1, UserId.Anonymous, When, "AUTH.TEST", "two", Ref: null));

        Assert.True(one > 0);
        Assert.True(two > one); // never the -1 placeholder, never duplicated
        Assert.Equal(one, store.Audits[0].Sequence); // in-memory mirror matches the DB
        Assert.Equal(two, store.Audits[1].Sequence);

        using var connection = new SqliteConnection($"Data Source={DbPath(root)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"sequence\" FROM \"AuditEvents\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(one, reader.GetInt64(0));
        Assert.True(reader.Read());
        Assert.Equal(two, reader.GetInt64(0));
        connection.Close();
    }

    [Fact]
    public async Task UpgradeFromMigration01_AuditRowsPreserved_AndRenumberedToDistinctIdentities()
    {
        string root = NewRoot();
        string dbPath = await LedgerSchemaFixture.BuildAsync(root); // migration 01, one -1 audit row
        long before = await SqliteTest.CountAsync(dbPath, "AuditEvents");

        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        Assert.Equal(before, await SqliteTest.CountAsync(dbPath, "AuditEvents")); // row preserved
        long min = (await SqliteTest.MinAsync(dbPath, "AuditEvents", "sequence"))!.Value;
        long max = (await SqliteTest.MinAsync(dbPath, "AuditEvents", "sequence"))!.Value;
        Assert.Equal(1, min);   // the -1 placeholder became a real identity
        Assert.Equal(1, max);   // single row, distinct identity

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"message\", \"terminal\", \"reason\" FROM \"AuditEvents\"";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("seeded fixture day", reader.GetString(0));
        Assert.True(reader.IsDBNull(1)); // terminal null for legacy rows
        Assert.True(reader.IsDBNull(2)); // reason null for legacy rows
        connection.Close();
    }

    private static async Task<Dictionary<string, int>> TableInfoAsync(string dbPath, string table)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(1)] = reader.GetInt32(5); // name -> pk flag
        }

        await connection.CloseAsync();
        return result;
    }
}