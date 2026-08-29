using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;

namespace BentaGo.Application.Import;

/// <summary>
/// Pure client-file mapping (BG-005b locked decision 4): a
/// <see cref="CsvProductFile"/> becomes an <see cref="ImportPreview"/> with
/// derived SKUs, per-row errors, and the whole-file summary. No I/O, no store
/// access — the same function feeds PreviewAsync and the import pass.
///
/// Collision semantics (reconciling locked decisions 1 and 5): a derived SKU
/// of an otherwise-valid row that duplicates an EARLIER valid row in the same
/// file is a specific per-row error and that row is excluded (fixture:
/// "duplicate derived SKU", "barcode1 collision with another file row").
/// Existing CATALOG SKUs are NOT errors — the import contract is UPSERT on sku
/// for re-imports (decision 5), so a re-sent file updates in place and never
/// creates duplicate rows.
/// </summary>
public static class ProductCsvMapper
{
    public static ImportPreview Map(CsvProductFile file)
    {
        IReadOnlyList<int>? header = ProductCsvProfile.ResolveHeader(file.HeaderNames);
        if (header is null)
        {
            return new ImportPreview(
                file.SourceName,
                new ImportFileSummary(
                    TotalRows: 0,
                    ValidRows: 0,
                    ErrorRows: 0,
                    FileErrors: new[]
                    {
                        new DomainError("IMPORT.BAD_HEADER",
                            "the file header does not match the 17-column product profile " +
                            "(missing, duplicated, or unknown columns)"),
                    }),
                Array.Empty<ProductImportRow>());
        }

        var rows = new List<ProductImportRow>(file.Rows.Count);
        var claimedSkus = new HashSet<string>(StringComparer.Ordinal);
        int total = 0, valid = 0, error = 0;

        foreach (CsvProductRow row in file.Rows)
        {
            total++;
            ProductImportRow mapped = MapRow(row, header, claimedSkus);
            if (mapped.Errors.Count == 0)
            {
                valid++;
            }
            else
            {
                error++;
            }

            rows.Add(mapped);
        }

        return new ImportPreview(
            file.SourceName,
            new ImportFileSummary(total, valid, error, FileErrors: Array.Empty<DomainError>()),
            rows);
    }

    private static ProductImportRow MapRow(CsvProductRow row, IReadOnlyList<int> header, ISet<string> claimedSkus)
    {
        if (row.Fields.Count != ProductCsvProfile.ColumnCount)
        {
            return new ProductImportRow(
                row.LineNumber,
                Sku: string.Empty,
                Name: null, Barcode1: null, Description: null, Category: null,
                Unit: string.Empty,
                RetailPrice: null, WholesalePrice: null, Cost: null,
                HasExpiry: false, ExpirationDate: null,
                SupportsPwdDiscount: false, SupportsSeniorDiscount: false,
                PwdFlagRaw: null, PwdPercentRaw: null, SeniorFlagRaw: null,
                StockQty: null, StockQtyNonPositive: false,
                Errors: new[]
                {
                    new DomainError("IMPORT.ROW_SHAPE",
                        $"row {row.LineNumber} has {row.Fields.Count} columns; the product profile has " +
                        $"{ProductCsvProfile.ColumnCount}", "row"),
                });
        }

        string Field(string column) => row.Fields[header[IndexOf(column)]].Trim();

        var errors = new List<DomainError>();

        string name = Field(ProductCsvProfile.Name);
        if (name.Length == 0)
        {
            errors.Add(new DomainError("IMPORT.NAME_EMPTY",
                $"row {row.LineNumber} has an empty name; fill it and re-import", "name"));
        }

        // Retail is REQUIRED — an empty selling price is invalid for selling
        // (locked: rp empty => row error).
        string retailCell = Field(ProductCsvProfile.RpInclusive);
        Money? retail = null;
        if (retailCell.Length == 0)
        {
            errors.Add(new DomainError("IMPORT.RETAIL_EMPTY",
                $"row {row.LineNumber} has an empty retail price (rp_inclusive); a sellable product needs one", "rp_inclusive"));
        }
        else if (!ProductCsvValues.TryParseMoney(retailCell, out Money parsedRetail))
        {
            errors.Add(new DomainError("IMPORT.MONEY_INVALID",
                $"row {row.LineNumber} retail '{retailCell}' is not a valid amount (non-negative, at most 2 decimals)",
                "rp_inclusive"));
        }
        else
        {
            retail = parsedRetail;
        }

        // Wholesale: 0 => null (locked); empty => null; anything unparseable is
        // a specific error, never a silent correction.
        string wholesaleCell = Field(ProductCsvProfile.WpInclusive);
        Money? wholesale = null;
        if (wholesaleCell.Length > 0)
        {
            if (!ProductCsvValues.TryParseMoney(wholesaleCell, out Money parsedWholesale))
            {
                errors.Add(new DomainError("IMPORT.MONEY_INVALID",
                    $"row {row.LineNumber} wholesale '{wholesaleCell}' is not a valid amount " +
                    "(non-negative, at most 2 decimals; 0 means no wholesale price)", "wp_inclusive"));
            }
            else if (parsedWholesale.Centavos != 0)
            {
                wholesale = parsedWholesale;
            }
        }

        string costCell = Field(ProductCsvProfile.Cost);
        Money? cost = null;
        if (costCell.Length > 0)
        {
            if (!ProductCsvValues.TryParseMoney(costCell, out Money parsedCost))
            {
                errors.Add(new DomainError("IMPORT.MONEY_INVALID",
                    $"row {row.LineNumber} cost '{costCell}' is not a valid amount (non-negative, at most 2 decimals)",
                    "cost"));
            }
            else
            {
                cost = parsedCost;
            }
        }

        string hasExpiryCell = Field(ProductCsvProfile.HasExpiry);
        bool hasExpiry = false;
        if (hasExpiryCell.Length > 0 && !ProductCsvValues.TryParseBool(hasExpiryCell, out hasExpiry))
        {
            errors.Add(new DomainError("IMPORT.BAD_HAS_EXPIRY",
                $"row {row.LineNumber} has_expiry '{hasExpiryCell}' is not True or False", "has_expiry"));
        }

        string expiryCell = Field(ProductCsvProfile.ExpirationDate);
        DateOnly? expirationDate = null;
        if (expiryCell.Length > 0)
        {
            if (!ProductCsvValues.TryParseExpiryDate(expiryCell, out DateOnly parsedExpiry))
            {
                errors.Add(new DomainError("IMPORT.DATE_INVALID",
                    $"row {row.LineNumber} expiration_date '{expiryCell}' is not a valid M/D/YYYY date",
                    "expiration_date"));
            }
            else
            {
                expirationDate = parsedExpiry;
            }
        }

        string? barcode1 = NullIfEmpty(Field(ProductCsvProfile.Barcode1));
        // barcode2 stays DORMANT (locked): parsed for shape, never imported or stored.
        string? description = NullIfEmpty(Field(ProductCsvProfile.Description));
        string? category = NullIfEmpty(Field(ProductCsvProfile.Category));

        string sku = ProductSkuDerivation.Derive(barcode1, name);

        string pwdRaw = Field(ProductCsvProfile.SDiscPwdSc);
        string pwdPercentRaw = Field(ProductCsvProfile.SPwdScPerc);
        string seniorRaw = Field(ProductCsvProfile.SDiscAth);
        bool supportsPwd = ProductCsvValues.PresentAndNonZero(pwdRaw)
            || ProductCsvValues.PresentAndNonZero(pwdPercentRaw);
        bool supportsSenior = ProductCsvValues.PresentAndNonZero(seniorRaw);

        string stockQtyCell = Field(ProductCsvProfile.StockQty);
        bool stockQtyNonPositive = decimal.TryParse(
            stockQtyCell, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out decimal stockQty)
            && stockQty <= 0m;

        // Collision scope = THIS file: a valid row whose derived SKU was
        // already claimed by an earlier valid row is a specific error and is
        // excluded; the first occurrence wins (locked decision 1).
        if (errors.Count == 0 && !claimedSkus.Add(sku))
        {
            errors.Add(new DomainError("IMPORT.SKU_DUPLICATE",
                $"row {row.LineNumber} derived SKU '{sku}' already appears in this file; " +
                "the first occurrence is kept and this row is skipped", "sku"));
        }

        return new ProductImportRow(
            row.LineNumber,
            sku,
            name.Length == 0 ? null : name,
            barcode1,
            description,
            category,
            new Category(category ?? string.Empty).DefaultUnit,
            retail,
            wholesale,
            cost,
            hasExpiry,
            expirationDate,
            supportsPwd,
            supportsSenior,
            NullIfEmpty(pwdRaw),
            NullIfEmpty(pwdPercentRaw),
            NullIfEmpty(seniorRaw),
            NullIfEmpty(stockQtyCell),
            stockQtyNonPositive,
            errors);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static int IndexOf(string column)
    {
        for (int i = 0; i < ProductCsvProfile.Columns.Count; i++)
        {
            if (string.Equals(ProductCsvProfile.Columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentException($"unknown profile column '{column}'", nameof(column));
    }
}