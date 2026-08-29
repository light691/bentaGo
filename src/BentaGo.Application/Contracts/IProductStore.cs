using BentaGo.Core.Common;
using BentaGo.Core.Domain.Catalog;

namespace BentaGo.Application.Contracts;

/// <summary>
/// The catalog persistence seam (BG-005b). Upsert-by-SKU is the locked import
/// contract: a re-imported file updates existing products in place and never
/// creates duplicate rows. The v2 Product record is ADDITIVE over v1 — the
/// table carries the full mapped contract (sku required, wholesale 0 => null,
/// barcode2 dormant, no stock_qty column — inventory comes from movements in
/// BG-005c).
/// </summary>
public interface IProductStore
{
    /// <summary>
    /// Inserts the product when its SKU is new, otherwise updates the existing
    /// catalog row in place. Returns true when a row was inserted, false when
    /// an existing row was updated.
    /// </summary>
    Task<bool> UpsertBySkuAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Finds a catalog product by its derived SKU, or null.</summary>
    Task<Product?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default);

    /// <summary>All catalog products, ordered by SKU.</summary>
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken cancellationToken = default);
}
