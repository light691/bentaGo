using BentaGo.Application.Contracts;
using BentaGo.Core.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// EF-backed catalog store (BG-005b). Upsert-by-SKU is the locked import
/// contract: the same file re-sent updates existing products in place and
/// never creates duplicate rows (the Sku column is UNIQUE). The v2 product
/// contract is stored as-is (sku required, barcode1 optional unique, wholesale
/// 0 => null, no stock_qty — inventory comes from movements in BG-005c).
/// </summary>
public sealed class ProductStore(IDbContextFactory<BentaGoDbContext> factory) : IProductStore
{
    public async Task<bool> UpsertBySkuAsync(Product product, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);

        Product? existing = await context.Products
            .SingleOrDefaultAsync(p => p.Sku == product.Sku, cancellationToken);

        if (existing is null)
        {
            context.Products.Add(product);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        // Idempotent re-import: update the existing row in place, keeping its
        // stable identity (docs/02: internal UUID; products are never
        // hard-deleted or re-created).
        context.Entry(existing).CurrentValues.SetValues(product with { Id = existing.Id });
        await context.SaveChangesAsync(cancellationToken);
        return false;
    }

    public async Task<Product?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Products.SingleOrDefaultAsync(p => p.Sku == sku, cancellationToken);
    }

    public async Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        // Exact TEXT equality — leading zeros are significant by construction
        // (barcodes are verbatim strings, never rewrapped as numbers; the
        // IX_Products_Barcode unique index serves this).
        return await context.Products.SingleOrDefaultAsync(p => p.Barcode == barcode, cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> SearchByNameAsync(
        string namePart, int limit, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        string pattern = "%" + EscapeLike(namePart) + "%";
        return await context.Products
            .Where(p => EF.Functions.Like(p.Name, pattern))
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Sku)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Products
            .OrderBy(p => p.Sku)
            .ToListAsync(cancellationToken);
    }
}
