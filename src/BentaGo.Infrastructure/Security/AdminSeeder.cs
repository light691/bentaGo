using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Security;

public interface IAdminSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Seeds the initial administrator (username 'admin', BCrypt hash of 'admin',
/// Role.Admin) as a real Users row on FIRST RUN ONLY (BG-005a). The admin
/// User row is created only when the Users table has NO rows at all — once
/// any user exists the seeder never touches Users (a deactivated admin stays
/// deactivated by design; F6). Idempotent: a second run inserts nothing, and
/// the hash is computed at most once per run even when both rows need it. The
/// legacy AppSettings seed.* rows are ALSO kept (restore verification and
/// bootstrap tests rely on them) but are no longer consulted by login —
/// existing data roots that carry them keep working untouched, and a fresh
/// root still satisfies the BG-001 first-run contract.
/// </summary>
public sealed class AdminSeeder : IAdminSeeder
{
    private const string UsernameKey = "seed.admin.username";
    private const string PasswordHashKey = "seed.admin.passwordHash";
    private const string AdminUsername = "admin";

    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly IPasswordHasher _passwordHasher;

    public AdminSeeder(IDbContextFactory<BentaGoDbContext> factory, IPasswordHasher passwordHasher)
    {
        _factory = factory;
        _passwordHasher = passwordHasher;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        string? adminHash = null;
        string Hash() => adminHash ??= _passwordHasher.Hash("admin");

        bool hasAppRows = await context.AppSettings
            .AnyAsync(s => s.Key == UsernameKey, cancellationToken);
        if (!hasAppRows)
        {
            context.AppSettings.Add(new AppSettingKeyValue { Key = UsernameKey, Value = AdminUsername });
            context.AppSettings.Add(new AppSettingKeyValue { Key = PasswordHashKey, Value = Hash() });
        }

        // FIRST RUN ONLY (F6 decision): seed the admin account only when no
        // Users rows exist. A deactivated admin row therefore stays
        // deactivated — the seeder never resurrects accounts.
        bool anyUsers = await context.Users.AnyAsync(cancellationToken);
        if (!anyUsers)
        {
            context.Users.Add(new User
            {
                Id = new UserId(Guid.NewGuid()),
                Username = AdminUsername,
                PasswordHash = Hash(),
                Role = Role.Admin,
                FailedAttempts = 0,
                LockedUntilUtc = null,
                DeactivatedUtc = null,
                CreatedBy = UserId.Anonymous,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (SqliteException)
        {
            // A concurrent boot seeded the same rows between our check and
            // save. The rows now exist, which is the wanted end state.
        }
    }
}
