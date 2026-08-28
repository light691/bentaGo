using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Creates <see cref="BentaGoDbContext"/> instances from a fixed SQLite
/// connection string (the data directory path comes from the host wiring).
/// </summary>
public sealed class BentaGoDbContextFactory : IDbContextFactory<BentaGoDbContext>
{
    private readonly DbContextOptions<BentaGoDbContext> _options;

    public BentaGoDbContextFactory(string connectionString)
    {
        _options = new DbContextOptionsBuilder<BentaGoDbContext>()
            .UseSqlite($"{connectionString};Pooling=False")
            .Options;
    }

    public BentaGoDbContext CreateDbContext() => new(_options);
}
