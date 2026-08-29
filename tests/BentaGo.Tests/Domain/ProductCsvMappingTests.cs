using BentaGo.Application.Import;
using BentaGo.Core.Domain;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// Client-file mapping + validation (BG-005b locked decision 4): a parsed CSV
/// file becomes a preview with derived SKUs, per-row errors, and the file
/// summary. Rows with errors are excluded; valid rows import. Collisions are
/// scoped to THIS file (first occurrence wins); existing catalog SKUs upsert
/// at import time (decision 5).
/// </summary>
public sealed class ProductCsvMappingTests
{
    private static readonly string[] Header =
    {
        "name", "description", "Category", "rp_inclusive", "wp_inclusive", "barcode1", "barcode2",
        "Warehouse", "s_discR", "s_discPWD_SC", "s_PWD_SC_perc", "s_discAth", "s_ask_qty",
        "stock_qty", "cost", "has_expiry", "expiration_date",
    };

    private static CsvProductFile File(params string[][] rows)
    {
        var data = new List<CsvProductRow>();
        for (int i = 0; i < rows.Length; i++)
        {
            data.Add(new CsvProductRow(i + 2, rows[i])); // header = line 1, data starts line 2
        }

        return new CsvProductFile("products.csv", Header, data);
    }

    /// <summary>A 17-cell valid row with the given overrides.</summary>
    private static string[] Row(
        string name = "Del Monte ACE Pineapple Juice 220ml",
        string? rp = "39.00",
        string? wp = "39.00",
        string? barcode1 = "4806515910543",
        string? stockQty = "11.00",
        string? hasExpiry = "False",
        string? expiry = "6/18/2026 12:00:00 AM",
        string? category = "Canned Juices",
        string? discPwd = "False",
        string? pwdPerc = "0.00",
        string? discAth = "False") =>
        new[]
        {
            name, name, category ?? "", rp ?? "", wp ?? "", barcode1 ?? "", "",
            "Stock", "False", discPwd ?? "", pwdPerc ?? "", discAth ?? "", "True",
            stockQty ?? "", "29.96", hasExpiry ?? "", expiry ?? "",
        };

    [Fact]
    public void ValidRow_MapsAllContractFields()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row()));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.IsValid);
        Assert.Equal("4806515910543", row.Sku);            // barcode digits verbatim
        Assert.Equal("Del Monte ACE Pineapple Juice 220ml", row.Name);
        Assert.Equal("Canned Juices", row.Category);
        Assert.Equal("piece", row.Unit);                   // category default
        Assert.Equal(new Money(3900), row.RetailPrice);
        Assert.Equal(new Money(3900), row.WholesalePrice);
        Assert.Equal(new Money(2996), row.Cost);
        Assert.False(row.HasExpiry);
        Assert.Equal(new DateOnly(2026, 6, 18), row.ExpirationDate); // date still parsed
        Assert.False(row.SupportsPwdDiscount);
        Assert.False(row.SupportsSeniorDiscount);
        Assert.Equal("11.00", row.StockQty);               // informational only
        Assert.False(row.StockQtyNonPositive);
        Assert.Equal("products.csv", preview.SourceName);
        Assert.True(preview.Summary.Ok);
        Assert.Equal(1, preview.Summary.TotalRows);
        Assert.Equal(1, preview.Summary.ValidRows);
        Assert.Equal(0, preview.Summary.ErrorRows);
    }

    [Fact]
    public void EmptyRetail_IsRowError()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(rp: "")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == "IMPORT.RETAIL_EMPTY" && e.Field == "rp_inclusive");
        Assert.Null(row.RetailPrice);
        Assert.Equal(1, preview.Summary.ErrorRows);
        Assert.Equal(0, preview.Summary.ValidRows);
    }

    [Fact]
    public void WholesaleZero_MapsToNull_NotAnError()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(wp: "0.00")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.IsValid);
        Assert.Null(row.WholesalePrice);
    }

    [Fact]
    public void WholesaleNegative_IsBadMoney()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(wp: "-5.00")));
        Assert.Contains(Assert.Single(preview.Rows).Errors, e => e.Code == "IMPORT.MONEY_INVALID");
    }

    [Fact]
    public void NoBarcode1_DerivesNameSku()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(barcode1: "")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.IsValid);
        Assert.Null(row.Barcode1);
        Assert.StartsWith("X", row.Sku);
        Assert.Equal(1 + ProductSkuDerivation.HashHexLength, row.Sku.Length);
    }

    [Fact]
    public void DuplicateDerivedSku_SecondOccurrenceErrors_FirstWins()
    {
        var rows = new[] { Row(name: "Del Monte ACE Pineapple Juice 220ml", barcode1: ""), Row(name: "Del Monte ACE Pineapple Juice 220ml", barcode1: "") };
        ImportPreview preview = ProductCsvMapper.Map(File(rows));

        Assert.True(preview.Rows[0].IsValid);
        Assert.False(preview.Rows[1].IsValid);
        Assert.Equal(preview.Rows[0].Sku, preview.Rows[1].Sku);
        Assert.Contains(preview.Rows[1].Errors, e => e.Code == "IMPORT.SKU_DUPLICATE");
        Assert.Equal(1, preview.Summary.ValidRows);
        Assert.Equal(1, preview.Summary.ErrorRows);
    }

    [Fact]
    public void Barcode1CollisionWithAnotherFileRow_SecondOccurrenceErrors()
    {
        var rows = new[]
        {
            Row(name: "Coke Mismo", barcode1: "4801234567890"),
            Row(name: "Coke Big", barcode1: "4801234567890"), // same barcode1 => same derived SKU
        };
        ImportPreview preview = ProductCsvMapper.Map(File(rows));

        Assert.True(preview.Rows[0].IsValid);
        Assert.False(preview.Rows[1].IsValid);
        Assert.Contains(preview.Rows[1].Errors, e => e.Code == "IMPORT.SKU_DUPLICATE");
    }

    [Fact]
    public void SevenRowStyleQuotes_QuoteCharsSurviveInsideField()
    {
        // The client file carries 7 rows with literal inch-mark quotes like
        // "GILAS PAPER PLATE 9\"" mid-field — the parser keeps them; the
        // mapper must not choke and the name keeps the quote char.
        ImportPreview preview = ProductCsvMapper.Map(File(
            Row(name: "GILAS PAPER PLATE 9\"", rp: "35.00", wp: "0.00", barcode1: "4806525910543")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.IsValid);
        Assert.Equal("GILAS PAPER PLATE 9\"", row.Name);
        Assert.Equal("4806525910543", row.Sku);
    }

    [Fact]
    public void BadMoney_ThreeDecimals_IsRowError()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(rp: "65.005")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == "IMPORT.MONEY_INVALID" && e.Field == "rp_inclusive");
    }

    [Fact]
    public void BadDate_IsRowError()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(expiry: "2026-06-18"))); // ISO shape rejected
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == "IMPORT.DATE_INVALID" && e.Field == "expiration_date");
        Assert.Null(row.ExpirationDate);
    }

    [Fact]
    public void EmptyName_IsRowError()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(name: "  ")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == "IMPORT.NAME_EMPTY" && e.Field == "name");
        Assert.Null(row.Name);
    }

    [Fact]
    public void NegativeStockQty_InformationalOnly_ValidRow()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(Row(stockQty: "-21.00")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.IsValid);                    // stock_qty is not imported; never an error
        Assert.Equal("-21.00", row.StockQty);
        Assert.True(row.StockQtyNonPositive);        // flagged as junk/informational
    }

    [Fact]
    public void PwdSeniorFlags_DerivedFromNonZeroCells_RawKept()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(
            Row(discPwd: "True", pwdPerc: "0.00", discAth: "False")));
        ProductImportRow row = Assert.Single(preview.Rows);

        Assert.True(row.SupportsPwdDiscount);
        Assert.False(row.SupportsSeniorDiscount);
        Assert.Equal("True", row.PwdFlagRaw);        // raw values stay in preview (BG-005d wires policy)
        Assert.Equal("0.00", row.PwdPercentRaw);
        Assert.Equal("False", row.SeniorFlagRaw);
    }

    [Fact]
    public void PwdPercentNonZero_AlsoDerivesPwdSupport()
    {
        ImportPreview preview = ProductCsvMapper.Map(File(
            Row(discPwd: "False", pwdPerc: "20.00", discAth: "False")));
        Assert.True(Assert.Single(preview.Rows).SupportsPwdDiscount);
    }

    [Fact]
    public void HeaderOrderTolerant_MapsByHeaderName()
    {
        // Same 17 columns, shuffled: mapping must follow the header names.
        var shuffled = new[]
        {
            "barcode1", "name", "description", "Category", "rp_inclusive", "wp_inclusive",
            "barcode2", "Warehouse", "s_discR", "s_discPWD_SC", "s_PWD_SC_perc", "s_discAth",
            "s_ask_qty", "stock_qty", "cost", "has_expiry", "expiration_date",
        };
        var file = new CsvProductFile("products.csv", shuffled, new[]
        {
            new CsvProductRow(2, new[]
            {
                "4806515910543", "Del Monte ACE Pineapple Juice 220ml", "Del Monte ACE Pineapple Juice 220ml",
                "Canned Juices", "39.00", "39.00", "", "Stock", "False", "False", "0.00", "False",
                "True", "11.00", "29.96", "False", "6/18/2026 12:00:00 AM",
            }),
        });

        ImportPreview preview = ProductCsvMapper.Map(file);
        ProductImportRow row = Assert.Single(preview.Rows);
        Assert.True(row.IsValid);
        Assert.Equal("4806515910543", row.Sku);
        Assert.Equal(new Money(3900), row.RetailPrice);
    }

    [Fact]
    public void BadHeader_UnknownColumn_WholeFileError_NoRows()
    {
        var file = new CsvProductFile("products.csv",
            new[] { "name", "description", "Category", "rp_inclusive", "wp_inclusive", "barcode1", "barcode2",
                "Warehouse", "s_discR", "s_discPWD_SC", "s_PWD_SC_perc", "s_discAth", "s_ask_qty",
                "stock_qty", "cost", "has_expiry", "expiration_date", "extra" },
            Array.Empty<CsvProductRow>());

        ImportPreview preview = ProductCsvMapper.Map(file);
        Assert.False(preview.Summary.Ok);
        Assert.Contains(preview.Summary.FileErrors, e => e.Code == "IMPORT.BAD_HEADER");
        Assert.Empty(preview.Rows);
    }

    [Fact]
    public void BadRowShape_FewerThan17Columns_IsRowError()
    {
        var file = new CsvProductFile("products.csv", Header,
            new[] { new CsvProductRow(2, new[] { "only", "four", "cells", "here" }) });

        ImportPreview preview = ProductCsvMapper.Map(file);
        ProductImportRow row = Assert.Single(preview.Rows);
        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == "IMPORT.ROW_SHAPE");
        Assert.Equal(1, preview.Summary.ErrorRows);
    }
}