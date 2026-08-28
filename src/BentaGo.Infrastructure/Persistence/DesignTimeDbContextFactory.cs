using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Used by `dotnet ef` design-time tooling to create a context without
/// running the application. Points at a throwaway temp database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BentaGoDbContext>
{
    public BentaGoDbContext CreateDbContext(string[] args)
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"bentago-design-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<BentaGoDbContext>()
            .UseSqlite($"Data Source={tempDb};Pooling=False")
            .Options;
        return new BentaGoDbContext(options);
    }
}
