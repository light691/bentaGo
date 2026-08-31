using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
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
    public async Task FixtureAuditRow_Preserved_WithHardenedIdentitySchema()
    {
        // Retargeted for the BG-P1 schema reset (P1c): the upgrade path this
        // test exercised is gone — the ONE baseline migration already carries
        // the hardened AuditEvents (identity PK, terminal/reason), so the
        // seeded row keeps its real identity verbatim.
        string root = NewRoot();
        string dbPath = await LedgerSchemaFixture.BuildAsync(root);
        long before = await SqliteTest.CountAsync(dbPath, "AuditEvents");

        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        Assert.Equal(before, await SqliteTest.CountAsync(dbPath, "AuditEvents")); // row preserved
        long min = (await SqliteTest.MinAsync(dbPath, "AuditEvents", "sequence"))!.Value;
        Assert.Equal(1, min);   // the seeded row carries a real identity

        using var connection = new SqliteConnection($"Data Source={DbPath(root)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"message\", \"terminal\", \"reason\" FROM \"AuditEvents\"";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("seeded fixture day", reader.GetString(0));
        Assert.True(reader.IsDBNull(1)); // terminal null for the seeded row
        Assert.True(reader.IsDBNull(2)); // reason null for the seeded row
        connection.Close();
    }

    [Fact]
    public async Task BatchAppendedAudits_PersistRealIdentities_InMemoryMirrorStagesMinusOne()
    {
        // F4 parity (BG-005d): the SAME batch append behaves differently in
        // the two mirrors, and that is documented contract semantics, not a
        // drift to fix silently: the SQLite store commits batch audits with a
        // REAL sequence inside the batch transaction; the in-memory mirror
        // stages them verbatim (-1) because only RecordAudit assigns there.
        // Audit rendering is message-only, so neither mirror's sequence
        // granularity affects reports or goldens.
        var audit = new AuditEvent(-1, UserId.Anonymous, When, "AUTH.TEST", "batch-audit", Ref: null);

        // SQLite side (the persisted contract).
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var store = host.GetRequiredService<SqliteLedgerStore>();

        var product = new ProductId(Guid.NewGuid());
        Result<AppendOutcome> sqliteOutcome = store.Append(new AppendBatch(
            new Movement[] { Moves.GoodsReceived(0, When, UserId.Anonymous, new GoodsRef(new GoodsId(Guid.NewGuid())), product, StoreLocations.Warehouse, Quantity.Of(10)) },
            Audits: new[] { audit }));
        Assert.True(sqliteOutcome.IsSuccess, sqliteOutcome.Errors.FirstOrDefault()?.Code ?? "append failed");
        AuditEvent persisted = Assert.Single(store.Audits);
        Assert.True(persisted.Sequence > 0, "the DB identity must replace the -1 placeholder in a batch");

        // In-memory side (the documented staging semantics).
        var memory = new InMemoryLedgerStore();
        Result<AppendOutcome> memoryOutcome = memory.Append(new AppendBatch(
            new Movement[] { Moves.GoodsReceived(0, When, UserId.Anonymous, new GoodsRef(new GoodsId(Guid.NewGuid())), product, StoreLocations.Warehouse, Quantity.Of(10)) },
            Audits: new[] { audit }));
        Assert.True(memoryOutcome.IsSuccess, memoryOutcome.Errors.FirstOrDefault()?.Code ?? "append failed");
        AuditEvent staged = Assert.Single(memory.Audits);
        Assert.Equal(-1, staged.Sequence); // staged verbatim until RecordAudit/rebuild assigns

        Assert.Equal(persisted.Message, staged.Message); // semantics equal; identity granularity differs
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
