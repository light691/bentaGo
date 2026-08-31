using System.IO;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U8 — RestoreTests (docs/05 lane 10), retargeted to the BG-P1 schema reset
/// (P1c): the schema is ONE baseline migration, so there is no pre-upgrade
/// backup anymore — a restore cycle (schema-matching backup, the active
/// data) with the store closed re-opens to the exact seeded report — the
/// -wal/-shm companions move cleanly and the pre-restore copy is preserved.
/// F7 (BG-005d): restore verification also requires the USERS admin row —
/// AppSettings alone no longer proves a usable admin, because login reads
/// the Users table.
/// </summary>
public sealed class RestoreTests : PersistenceTestBase
{
    [Fact]
    public async Task RestoreCycle_RendersTheSeededFixtureExactly()
    {
        string root = NewRoot();
        await LedgerSchemaFixture.BuildAsync(root); // baseline + seeded mini-day

        // Pre-restore report (from a store over the fixture).
        var paths = new DataPathProvider(root);
        var preStore = new SqliteLedgerStore(paths, new DebugOptions());
        string preRender = LedgerSchemaFixture.Render(preStore).Text;
        preStore.Close();

        // The fixture root also gets the admin seed (the restore path
        // verifies it); the schema already sits at the single baseline head.
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<BentaGo.Infrastructure.Security.IAdminSeeder>().SeedAsync();

        // A schema-matching backup of the seeded database is restorable; the
        // host's restore hook closes the long-lived store first (R1), and the
        // -wal/-shm companions move with the old DB.
        var backupService = host.GetRequiredService<IBackupService>();
        BackupReceipt backup = await backupService.CreateAsync();
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(backup.BackupPath));

        var restoreService = host.GetRequiredService<IRestoreService>();
        RestoreReceipt restored = await restoreService.RestoreAsync(backup.BackupPath);

        Assert.True(restored.Verified);
        Assert.True(File.Exists(restored.PreRestoreCopyPath), "pre-restore copy should be preserved");
        Assert.False(File.Exists(DbPath(root) + "-wal"), "-wal companion should move with the old DB");
        Assert.False(File.Exists(DbPath(root) + "-shm"), "-shm companion should move with the old DB");

        // A re-opened store renders the seeded fixture report byte-for-byte.
        var afterStore = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        Assert.Equal(preRender, LedgerSchemaFixture.Render(afterStore).Text);
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(DbPath(root)));
    }

    [Fact]
    public async Task Restore_BackupMissingUsersAdminRow_FailsVerification_WithClearMessage()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();

        // Remove the admin USER row but keep the legacy AppSettings seed rows:
        // the schema-valid "appears seeded, unusable admin" backup (the F7 gap).
        await using (var context = await host
            .GetRequiredService<IDbContextFactory<BentaGoDbContext>>().CreateDbContextAsync())
        {
            User admin = await context.Users.SingleAsync(u => u.Username == "admin");
            context.Users.Remove(admin);
            await context.SaveChangesAsync();
        }

        var backupService = host.GetRequiredService<IBackupService>();
        BackupReceipt adminless = await backupService.CreateAsync();

        var restoreService = host.GetRequiredService<IRestoreService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => restoreService.RestoreAsync(adminless.BackupPath));

        Assert.Contains("Users", ex.Message);                 // names the failing check
        Assert.Contains("admin", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The failed restore rolled back: the active database is the
        // pre-restore copy, still present and intact (the file swap is
        // reverted atomically by the restore's rollback path).
        Assert.True(File.Exists(DbPath(root)), "active database must survive a failed verification");
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(DbPath(root)));
    }

    [Fact]
    public async Task Restore_AdminUserRow_AndLegacyAppSettings_PassesVerification()
    {
        string root = NewRoot();
        string dbPath = DbPath(root);
        var paths = new DataPathProvider(root);
        paths.EnsureCreated();

        // A baseline-era database: the ONE BG-P1 baseline migration (Users,
        // AuditEvents, full ledger) with the admin USER row AND the legacy
        // AppSettings seed rows — exactly what a seeded data root carries.
        IDbContextFactory<BentaGoDbContext> factory =
            new BentaGoDbContextFactory($"Data Source={dbPath};Pooling=False");
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = await factory.CreateDbContextAsync())
        {
            context.Users.Add(new User
            {
                Id = new UserId(Guid.NewGuid()),
                Username = "admin",
                PasswordHash = new BCryptPasswordHasher().Hash("admin"),
                Role = Role.Admin,
                FailedAttempts = 0,
                LockedUntilUtc = null,
                DeactivatedUtc = null,
                CreatedBy = UserId.Anonymous,
                CreatedUtc = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            });
            context.AppSettings.Add(new AppSettingKeyValue { Key = "seed.admin.username", Value = "admin" });
            context.AppSettings.Add(new AppSettingKeyValue { Key = "seed.admin.passwordHash", Value = "legacy-hash" });
            await context.SaveChangesAsync();
        }

        // The era-era backup (a plain copy of the closed, consistent database)
        // restores against an ACTIVE database at the same migration — both F7
        // checks (AppSettings row AND Users admin row) must pass.
        string backupPath = Path.Combine(paths.BackupsDir, "bentago-bg005a-era.db");
        File.Copy(dbPath, backupPath);

        var restoreService = new RestoreService(paths, factory);
        RestoreReceipt receipt = await restoreService.RestoreAsync(backupPath);

        Assert.True(receipt.Verified);
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(dbPath));
    }
}
