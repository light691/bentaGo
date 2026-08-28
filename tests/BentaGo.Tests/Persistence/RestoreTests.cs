using System.IO;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U8 — RestoreTests (docs/05 lane 10): the migration flow backs the
/// pre-upgrade fixture up FIRST; a restore cycle (schema-matching backup, the
/// active data) with the store closed re-opens to the exact pre-upgrade
/// report — the -wal/-shm companions move cleanly and the pre-restore copy is
/// preserved.
/// </summary>
public sealed class RestoreTests : PersistenceTestBase
{
    [Fact]
    public async Task RestoreCycle_RendersThePreUpgradeFixtureExactly()
    {
        string root = NewRoot();
        await LedgerSchemaFixture.BuildAsync(root); // migration 01 + seeded mini-day

        // Pre-upgrade report (from a store over the fixture).
        var paths = new DataPathProvider(root);
        var preStore = new SqliteLedgerStore(paths, new DebugOptions());
        string preRender = LedgerSchemaFixture.Render(preStore).Text;
        preStore.Close();

        // Upgrade to head: DbInitializer backs up the 01 fixture FIRST. The fixture
        // root also gets the admin seed (the restore path verifies it).
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<BentaGo.Infrastructure.Security.IAdminSeeder>().SeedAsync();

        string[] backups = Directory.GetFiles(Path.Combine(root, "backups"), "bentago-*.db");
        Assert.Single(backups);
        string preUpgradeBackup = backups[0];
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(preUpgradeBackup));

        // A schema-matching backup of the (upgraded, data-intact) database is
        // restorable; the host's restore hook closes the long-lived store
        // first (R1), and the -wal/-shm companions move with the old DB.
        var backupService = host.GetRequiredService<IBackupService>();
        BackupReceipt postUpgrade = await backupService.CreateAsync();

        var restoreService = host.GetRequiredService<IRestoreService>();
        RestoreReceipt restored = await restoreService.RestoreAsync(postUpgrade.BackupPath);

        Assert.True(restored.Verified);
        Assert.True(File.Exists(restored.PreRestoreCopyPath), "pre-restore copy should be preserved");
        Assert.False(File.Exists(DbPath(root) + "-wal"), "-wal companion should move with the old DB");
        Assert.False(File.Exists(DbPath(root) + "-shm"), "-shm companion should move with the old DB");

        // A re-opened store renders the pre-upgrade fixture report byte-for-byte.
        var afterStore = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        Assert.Equal(preRender, LedgerSchemaFixture.Render(afterStore).Text);
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(DbPath(root)));
    }
}
