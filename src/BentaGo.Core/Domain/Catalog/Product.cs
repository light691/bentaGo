namespace BentaGo.Core.Domain.Catalog;

/// <summary>
/// Catalog product record v2 (BG-005b) — ADDITIVE over the v1 contract
/// (sku,barcode,name,unit,price): the v1 fields are kept and the client-file
/// mapping adds description, category, wholesale price, cost, expiry
/// tracking, and PWD/senior discount eligibility.
///
/// Mapping rules (locked, do not re-litigate):
///   sku        required, derived in the client-mapping layer (never a file
///              column) — barcode1 digits verbatim, else "X" + 12-hex SHA-1
///              of the normalized name;
///   barcode    = barcode1, optional; barcode2 stays DORMANT (not imported,
///              not stored; documented as future);
///   name       trimmed;
///   retail     = rp_inclusive (required; empty is a row error);
///   wholesale  = wp_inclusive (0 => null);
///   cost       as decimal money;
///   category   trimmed, no case normalization;
///   unit       category-default ("piece");
///   expiry     = expiration_date parsed with explicit culture (M/D/YYYY);
///   has_expiry drives the expiry-tracking flag;
///   PWD/senior flags derived from s_discPWD_SC / s_PWD_SC_perc / s_discAth
///              being present-and-nonzero.
///
/// stock_qty is NOT a product fact (62% negative = junk): it is surfaced in
/// the import preview as an informational column only, never stored here.
/// Inventory enters via movements (BG-005c).
/// </summary>
public sealed record Product(
    ProductId Id,
    string Sku,
    string? Barcode,
    string Name,
    string? Description,
    Category Category,
    string Unit,
    Money RetailPrice,
    Money? WholesalePrice,
    Money? Cost,
    bool HasExpiry,
    DateOnly? ExpirationDate,
    bool SupportsPwdDiscount,
    bool SupportsSeniorDiscount);
