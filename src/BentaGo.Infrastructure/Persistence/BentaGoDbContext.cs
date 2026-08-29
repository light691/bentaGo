using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

public sealed class BentaGoDbContext : DbContext
{
    public DbSet<AppSettingKeyValue> AppSettings => Set<AppSettingKeyValue>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Peddler> Peddlers => Set<Peddler>();

    public DbSet<Product> Products => Set<Product>();

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

        // BG-005a identity schema: users with nullable direct peddler link
        // (decided; no FK constraint — Employee entity deferred) and peddlers.
        // Branded ids and the Role enum are stored TEXT; auth state columns
        // (FailedAttempts/LockedUntilUtc/DeactivatedUtc) live on the row.
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id)
                .HasConversion(id => id.Value.ToString("D"), s => new UserId(Guid.Parse(s)));
            entity.Property(u => u.Username).IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.Role).HasConversion<string>();
            entity.Property(u => u.PeddlerLink)
                .HasConversion(
                    link => link.HasValue ? link.Value.Value.ToString("D") : null,
                    s => s == null ? null : new PeddlerId(Guid.Parse(s)));
            entity.Property(u => u.FailedAttempts);
            entity.Property(u => u.LockedUntilUtc);
            entity.Property(u => u.DeactivatedUtc);
            entity.Property(u => u.CreatedBy)
                .HasConversion(id => id.Value.ToString("D"), s => new UserId(Guid.Parse(s)));
            entity.Property(u => u.CreatedUtc);
        });

        modelBuilder.Entity<Peddler>(entity =>
        {
            entity.ToTable("Peddlers");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id)
                .HasConversion(id => id.Value.ToString("D"), s => new PeddlerId(Guid.Parse(s)));
            entity.Property(p => p.DisplayName).IsRequired();
            entity.Property(p => p.Active);
        });

        // BG-005b catalog: the additive v2 product contract. SKU is the stable
        // business key (unique; derived in the client-mapping layer — never a
        // file column). barcode1 optional, unique when present; barcode2 is
        // DORMANT (not stored). stock_qty is intentionally absent — inventory
        // comes from movements (BG-005c), never from the import file.
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id)
                .HasConversion(id => id.Value.ToString("D"), s => new ProductId(Guid.Parse(s)));
            entity.Property(p => p.Sku).IsRequired();
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.Property(p => p.Barcode);
            entity.HasIndex(p => p.Barcode).IsUnique();
            entity.Property(p => p.Name).IsRequired();
            entity.Property(p => p.Description);
            entity.Property(p => p.Category)
                .HasConversion(c => c.Name, s => new Category(s));
            entity.Property(p => p.Unit).IsRequired();
            entity.Property(p => p.RetailPrice)
                .HasConversion(m => m.Centavos, c => new Money(c));
            entity.Property(p => p.WholesalePrice)
                .HasConversion(
                    m => m.HasValue ? m.Value.Centavos : (long?)null,
                    c => c.HasValue ? new Money(c.Value) : null);
            entity.Property(p => p.Cost)
                .HasConversion(
                    m => m.HasValue ? m.Value.Centavos : (long?)null,
                    c => c.HasValue ? new Money(c.Value) : null);
            entity.Property(p => p.HasExpiry);
            entity.Property(p => p.ExpirationDate);
            entity.Property(p => p.SupportsPwdDiscount);
            entity.Property(p => p.SupportsSeniorDiscount);
        });
    }
}
