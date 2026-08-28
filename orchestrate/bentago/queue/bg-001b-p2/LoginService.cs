using BentaGo.Application.Contracts;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Security;

/// <summary>
/// Placeholder user store backed by the seeded admin key/value rows.
/// BG-005 replaces this with real users and roles.
/// </summary>
public sealed class LoginService : ILoginService
{
    private const string UsernameKey = "seed.admin.username";
    private const string PasswordHashKey = "seed.admin.passwordHash";

    private readonly IDbContextFactory<BentaGoDbContext> _factory;
    private readonly IPasswordHasher _passwordHasher;

    public LoginService(IDbContextFactory<BentaGoDbContext> factory, IPasswordHasher passwordHasher)
    {
        _factory = factory;
        _passwordHasher = passwordHasher;
    }

    public async Task<bool> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || password is null)
        {
            return false;
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var rows = await context.AppSettings
            .Where(s => s.Key == UsernameKey || s.Key == PasswordHashKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        if (!rows.TryGetValue(UsernameKey, out var expectedUsername) ||
            !rows.TryGetValue(PasswordHashKey, out var hash))
        {
            return false;
        }

        return string.Equals(expectedUsername, username, StringComparison.Ordinal)
               && _passwordHasher.Verify(password, hash);
    }
}
