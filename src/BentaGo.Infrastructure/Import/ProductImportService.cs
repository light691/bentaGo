using BentaGo.Application.Contracts;
using BentaGo.Application.Import;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;

namespace BentaGo.Infrastructure.Import;

/// <summary>
/// Infrastructure import service (BG-005b Commit 2): parses a client file via
/// <see cref="ProductCsvParser"/>, maps it via the pure
/// <see cref="ProductCsvMapper"/>, and commits valid rows through
/// <see cref="IProductStore"/> with upsert-by-SKU semantics. Error rows are
/// excluded and the rest imports (locked decision 5); a re-sent file updates
/// in place and never duplicates rows (idempotent re-import).
/// </summary>
public sealed class ProductImportService(IProductStore store) : IProductImportService
{
    public Task<ImportPreview> PreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(filePath);
        return Task.FromResult(MapStream(stream, filePath));
    }

    public Task<ImportPreview> PreviewAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MapStream(stream, "(stream)"));
    }

    public async Task<ImportCommitResult> ImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(filePath);
        return await ImportStreamAsync(stream, filePath, cancellationToken);
    }

    public async Task<ImportCommitResult> ImportAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        return await ImportStreamAsync(stream, "(stream)", cancellationToken);
    }

    private static ImportPreview MapStream(Stream stream, string sourceName)
    {
        CsvProductFile file = ProductCsvParser.Parse(stream, sourceName);
        return ProductCsvMapper.Map(file);
    }

    private async Task<ImportCommitResult> ImportStreamAsync(
        Stream stream,
        string sourceName,
        CancellationToken cancellationToken)
    {
        ImportPreview preview = MapStream(stream, sourceName);
        if (!preview.Summary.Ok)
        {
            return new ImportCommitResult(preview, Inserted: 0, Updated: 0);
        }

        int inserted = 0, updated = 0;
        foreach (ProductImportRow row in preview.Rows)
        {
            if (!row.IsValid)
            {
                continue; // error rows are excluded; the rest imports
            }

            var product = new Product(
                Id: new ProductId(Guid.NewGuid()),
                Sku: row.Sku,
                Barcode: row.Barcode1,
                Name: row.Name ?? "",
                Description: row.Description,
                Category: new Category(row.Category ?? ""),
                Unit: row.Unit,
                RetailPrice: row.RetailPrice ?? Money.Zero,
                WholesalePrice: row.WholesalePrice,
                Cost: row.Cost,
                HasExpiry: row.HasExpiry,
                ExpirationDate: row.ExpirationDate,
                SupportsPwdDiscount: row.SupportsPwdDiscount,
                SupportsSeniorDiscount: row.SupportsSeniorDiscount);

            bool wasInserted = await store.UpsertBySkuAsync(product, cancellationToken);
            if (wasInserted)
            {
                inserted++;
            }
            else
            {
                updated++;
            }
        }

        return new ImportCommitResult(preview, inserted, updated);
    }
}