using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Security;

public interface IAdminSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Seeds the initial administrator placeholder (username + bcrypt hash of
/// "admin"). Idempotent: a second run inserts nothing.
/// </summary>
public sealed class AdminSeeder : IAdminSeeder
{
    private const string UsernameKey = "seed.admin.username";
    private const string PasswordHashKey = "seed.admin.passwordHash";

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

        bool alreadySeeded = await context.AppSettings
            .AnyAsync(s => s.Key == UsernameKey, cancellationToken);
        if (alreadySeeded)
        {
            return;
        }

        context.AppSettings.Add(new AppSettingKeyValue { Key = UsernameKey, Value = "admin" });
        context.AppSettings.Add(new AppSettingKeyValue
        {
            Key = PasswordHashKey,
            Value = _passwordHasher.Hash("admin"),
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
