using BentaGo.Core.Common;
using BentaGo.Core.Domain;

namespace BentaGo.Application.Import;

/// <summary>
/// One mapped preview row. The derived SKU is ALWAYS shown (locked decision
/// 1); the informational stock_qty cell is surfaced but never imported;
/// PWD/senior raw cells are kept for BG-005d policy wiring. A row with no
/// errors is eligible for import; every row with errors is excluded while the
/// rest of the file imports (locked decision 5 — not all-or-nothing).
/// </summary>
public sealed record ProductImportRow(
    int LineNumber,
    string Sku,
    string? Name,
    string? Barcode1,
    string? Description,
    string? Category,
    string Unit,
    Money? RetailPrice,
    Money? WholesalePrice,
    Money? Cost,
    bool HasExpiry,
    DateOnly? ExpirationDate,
    bool SupportsPwdDiscount,
    bool SupportsSeniorDiscount,
    string? PwdFlagRaw,
    string? PwdPercentRaw,
    string? SeniorFlagRaw,
    string? StockQty,
    bool StockQtyNonPositive,
    IReadOnlyList<DomainError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Whole-file summary: counts plus file-level (header) errors. Ok means the
/// header matched the 17-column profile exactly (order-tolerant) — per-row
/// errors never fail the file.
/// </summary>
public sealed record ImportFileSummary(
    int TotalRows,
    int ValidRows,
    int ErrorRows,
    IReadOnlyList<DomainError> FileErrors)
{
    public bool Ok => FileErrors.Count == 0;
}

/// <summary>
/// Result of a preview pass: the source name, the file summary, and the
/// per-row mapping (derived SKUs always present, errors per row). The import
/// service upserts exactly the Valid rows — re-imports update by SKU (locked
/// decision 5).
/// </summary>
public sealed record ImportPreview(
    string SourceName,
    ImportFileSummary Summary,
    IReadOnlyList<ProductImportRow> Rows);
