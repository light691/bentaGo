using BentaGo.Application.Import;
using BentaGo.Core.Common;

namespace BentaGo.Application.Contracts;

/// <summary>
/// Catalog product import seam (BG-005b). Preview is the pure, store-free
/// pass: <see cref="PreviewAsync"/> parses + maps a client file and returns
/// the derived SKUs, per-row errors, and file summary WITHOUT touching the
/// catalog. Import commits the preview: valid rows are upserted by SKU
/// (idempotent re-imports — the same file re-sent updates in place, never
/// duplicate rows); rows with errors are excluded and the rest imports. The
/// UI wires this service in BG-005d; it is intentionally NOT host-registered
/// until then.
/// </summary>
public interface IProductImportService
{
    /// <summary>Parses and maps the client file at <paramref name="filePath"/> into a preview.</summary>
    Task<ImportPreview> PreviewAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Parses and maps the client file from <paramref name="stream"/> into a preview.</summary>
    Task<ImportPreview> PreviewAsync(Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Previews <paramref name="filePath"/> then commits every valid row
    /// through the store (upsert by SKU). Error rows are never written.
    /// </summary>
    Task<ImportCommitResult> ImportAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Previews <paramref name="stream"/> then commits every valid row through
    /// the store (upsert by SKU). Error rows are never written.
    /// </summary>
    Task<ImportCommitResult> ImportAsync(Stream stream, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a catalog commit: which rows were inserted vs updated, plus the
/// preview that drove the import (derived SKUs, errors, summary). Error rows
/// are never written; valid rows are upserted by SKU.
/// </summary>
public sealed record ImportCommitResult(ImportPreview Preview, int Inserted, int Updated)
{
    public int Imported => Inserted + Updated;
}
