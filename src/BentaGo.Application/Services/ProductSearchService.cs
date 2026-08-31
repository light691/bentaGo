using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Identity;

namespace BentaGo.Application.Services;

/// <summary>
/// Offline product lookup (D8): exact SKU (derived SKUs are digits or
/// X+uppercase-hex, so the ordinal-ignore-case fallback covers typed
/// lowercase) → exact barcode (verbatim, leading zeros significant — TEXT
/// equality) → name infix (NOCASE, wildcard-escaped, capped). The keyboard-HID
/// scanner CR suffix is trimmed at this boundary. On-hand comes from the
/// movement fold at the single warehouse location.
/// </summary>
public sealed class ProductSearchService(
    ILedgerStore ledger,
    IProductStore products)
    : IProductSearchService
{
    public async Task<Result<IReadOnlyList<ProductHit>>> SearchAsync(
        string query, int limit = 25, CancellationToken ct = default)
    {
        string trimmed = query.Trim().TrimEnd('\r', '\n').Trim();
        if (trimmed.Length == 0)
        {
            return Result<IReadOnlyList<ProductHit>>.Ok(Array.Empty<ProductHit>());
        }

        // SKU exact (then the derived-SKU case mirror), then barcode exact.
        Product? product = await products.FindBySkuAsync(trimmed, ct);
        if (product is null && !IsAllUppercase(trimmed))
        {
            product = await products.FindBySkuAsync(trimmed.ToUpperInvariant(), ct);
        }

        if (product is null)
        {
            product = await products.FindByBarcodeAsync(trimmed, ct);
        }

        if (product is not null)
        {
            Quantity onHand = OnHandOf(product.Id);
            return Result<IReadOnlyList<ProductHit>>.Ok(new[]
            {
                new ProductHit(product, onHand),
            });
        }

        IReadOnlyList<Product> byName = await products.SearchByNameAsync(trimmed, limit, ct);
        var hits = new List<ProductHit>(byName.Count);
        foreach (Product named in byName)
        {
            hits.Add(new ProductHit(named, OnHandOf(named.Id)));
        }

        return Result<IReadOnlyList<ProductHit>>.Ok(hits);
    }

    private Quantity OnHandOf(ProductId product)
    {
        ledger.Bind();
        return ledger.State.StockOnHand(product, StoreLocations.Warehouse);
    }

    private static bool IsAllUppercase(string text) =>
        !text.Any(char.IsLower);
}
