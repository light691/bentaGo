using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Foundation;

/// <summary>
/// BG-005a seeding, retargeted to the BG-P1 schema reset (P1c): a fresh data
/// root boot seeds the admin Users row and the admin can authenticate
/// ('admin'/'admin'); a data root built by the seeded baseline fixture
/// (AppSettings seed rows + ledger rows, no Users yet) migrates to head
/// (a no-op over the ONE baseline), keeps the AppSettings rows and every
/// ledger row, and still authenticates the admin with the real user row.
/// </summary>
public sealed class IdentitySeedTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-seed-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FreshRoot_SeedsAdminUser_AndAdminAuthenticates()
    {
        ServiceProvider host = BentaGoHost.BuildForTests(_tempRoot);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var seeder = host.GetRequiredService<IAdminSeeder>();
        await seeder.SeedAsync();
        await seeder.SeedAsync(); // idempotent

        var auth = host.GetRequiredService<IAuthenticationService>();
        AuthResult login = await auth.AuthenticateAsync("admin", "admin");
        Assert.True(login.Success);
        Assert.Equal(Role.Admin, login.Role);

        await using var context = await host
            .GetRequiredService<IDbContextFactory<BentaGoDbContext>>().CreateDbContextAsync();
        Assert.Single(await context.Users.Where(u => u.Username == "admin").ToListAsync());
        var admin = await context.Users.SingleAsync(u => u.Username == "admin");
        Assert.Equal(Role.Admin, admin.Role);
        Assert.Equal(0, admin.FailedAttempts);
        Assert.Null(admin.DeactivatedUtc);
        Assert.False(string.IsNullOrEmpty(admin.PasswordHash));
    }

    [Fact]
    public async Task UpgradedBG001World_AdminAuthenticates_AppSettingsRowsAndLedgerRowsPreserved()
    {
        // 1. The baseline world: a real database at the ONE BG-P1 baseline
        // with a seeded mini-day (movements, documents, one audit row).
        string dbPath = await LedgerSchemaFixture.BuildAsync(_tempRoot);
        long movementsBefore = CountRows(dbPath, "Movements");
        long salesBefore = CountRows(dbPath, "Sales");
        long goodsBefore = CountRows(dbPath, "GoodsReceipts");

        // 2. The BG-001 world's seed: AppSettings rows, NO Users table yet.
        var factory = new BentaGoDbContextFactory($"Data Source={dbPath};Pooling=False");
        await using (var seedContext = factory.CreateDbContext())
        {
            seedContext.AppSettings.Add(new BentaGo.Core.Domain.AppSettingKeyValue { Key = "seed.admin.username", Value = "admin" });
            seedContext.AppSettings.Add(new BentaGo.Core.Domain.AppSettingKeyValue { Key = "seed.admin.passwordHash", Value = new BCryptPasswordHasher().Hash("admin") });
            await seedContext.SaveChangesAsync();
        }

        // 3. Migrate to head (a no-op over the single baseline) and seed:
        // additive, nothing lost.
        ServiceProvider host = BentaGoHost.BuildForTests(_tempRoot);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();

        var auth = host.GetRequiredService<IAuthenticationService>();
        AuthResult login = await auth.AuthenticateAsync("admin", "admin");
        Assert.True(login.Success);
        Assert.Equal(Role.Admin, login.Role);

        // AppSettings seed rows preserved (still present, no longer consulted).
        await using var context = await host
            .GetRequiredService<IDbContextFactory<BentaGoDbContext>>().CreateDbContextAsync();
        Assert.Equal(2, await context.AppSettings.CountAsync(s => s.Key.StartsWith("seed.admin.")));
        Assert.Equal("admin", await context.AppSettings
            .Where(s => s.Key == "seed.admin.username").Select(s => s.Value).SingleAsync());

        // No data loss in the ledger: every row survived verbatim.
        Assert.Equal(movementsBefore, CountRows(dbPath, "Movements"));
        Assert.Equal(salesBefore, CountRows(dbPath, "Sales"));
        Assert.Equal(goodsBefore, CountRows(dbPath, "GoodsReceipts"));
    }

    private static long CountRows(string dbPath, string table)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
        long count = Convert.ToInt64(command.ExecuteScalar());
        connection.Close();
        return count;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
