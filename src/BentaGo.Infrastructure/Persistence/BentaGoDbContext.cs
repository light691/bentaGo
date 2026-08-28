using BentaGo.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

public sealed class BentaGoDbContext : DbContext
{
    public DbSet<AppSettingKeyValue> AppSettings => Set<AppSettingKeyValue>();

    public BentaGoDbContext(DbContextOptions<BentaGoDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettingKeyValue>(entity =>
        {
            entity.ToTable("AppSettings");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Value).IsRequired();
        });
    }
}