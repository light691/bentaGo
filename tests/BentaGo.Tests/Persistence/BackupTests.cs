using System.IO;
using BentaGo.Application.Services;
using BentaGo.Infrastructure.Backup;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// U7 — BackupTests (docs/05 lane 9, backup half): an online backup taken
/// while the WAL store is live passes quick_check, hashes to its receipt, and
/// the backup file renders the same day as the live store.
/// </summary>
public sealed class BackupTests : PersistenceTestBase
{
    [Fact]
    public async Task OnlineBackup_WhileStoreLive_QuickCheckOk_ShaMatches_RendersSame()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var store = host.GetRequiredService<SqliteLedgerStore>();
        TwoPeddlersScenario.Run(store); // store stays live (WAL) during backup
        string liveRender = TwoPeddlersScenario.Render(store).Text;

        var backupService = host.GetRequiredService<IBackupService>();
        BackupReceipt receipt = await backupService.CreateAsync();

        Assert.True(File.Exists(receipt.BackupPath));
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(receipt.BackupPath));
        Assert.Equal(receipt.Sha256, await Task.Run(() => ReceiptSha256(receipt.BackupPath)));
        Assert.EndsWith("BG003_02_AddCheckoutIdempotency", receipt.DatabaseVersion);

        // The backup is a full consistency point: a store over the backup
        // file renders the exact same day.
        string scratch = NewRoot();
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(scratch, "data"));
        File.Copy(receipt.BackupPath, DbPath(scratch), overwrite: true);
        await BackupService.VerifyQuickCheckAsync(DbPath(scratch), CancellationToken.None);
        var backupStore = new SqliteLedgerStore(new DataPathProvider(scratch), new DebugOptions());
        Assert.Equal(liveRender, TwoPeddlersScenario.Render(backupStore).Text);
    }

    private static string ReceiptSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
