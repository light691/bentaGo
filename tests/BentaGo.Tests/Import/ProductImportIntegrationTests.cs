using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Import;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Import;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Import;

/// <summary>
/// BG-005b golden import run: the shipped schema-sampled fixture (22 data
/// rows: valid, empty retail, wholesale 0, no barcode1, duplicate derived
/// SKU, barcode1 collision, 7-row-style quotes, bad money, bad date, empty
/// name, negative stock_qty) -> preview with golden derived SKUs / error rows
/// / summary counts -> import -> catalog rows verify -> re-import idempotence
/// (same sku updates in place, no duplicate rows).
/// </summary>
public sealed class ProductImportIntegrationTests : PersistenceTestBase
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "products-bg005b.csv");

    private static (ProductImportService Service, ProductStore Store) NewSeam(ServiceProvider host)
    {
        // Not host-registered yet (BG-005d wires the UI): the import test
        // builds the dependency-light seam directly over the DbContextFactory.
        var store = new ProductStore(host.GetRequiredService<IDbContextFactory<BentaGoDbContext>>());
        return (new ProductImportService(store), store);
    }

    private ServiceProvider NewMigratedHost(string root, out ProductImportService service, out ProductStore store)
    {
        ServiceProvider host = NewHost(root);
        var initializer = host.GetRequiredService<DbInitializer>();
        initializer.EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
        (service, store) = NewSeam(host);
        return host;
    }

    [Fact]
    public async Task Fixture_Preview_MatchesGoldenExpectations()
    {
        using ServiceProvider host = NewMigratedHost(NewRoot(), out ProductImportService service, out _);

        ImportPreview preview = await service.PreviewAsync(FixturePath);

        Assert.True(preview.Summary.Ok);
        Assert.Equal("products-bg005b.csv", Path.GetFileName(preview.SourceName));
        Assert.Equal(22, preview.Summary.TotalRows);
        Assert.Equal(15, preview.Summary.ValidRows);
        Assert.Equal(7, preview.Summary.ErrorRows);

        // Derived SKUs are always shown — golden values.
        Assert.Equal("4806515910543", preview.Rows[0].Sku);   // barcode digits verbatim
        Assert.Equal("Xbd0422edfd55", preview.Rows[1].Sku);   // no barcode1 -> X + 12-hex SHA-1
        Assert.Equal("4806515910544", preview.Rows[2].Sku);
        Assert.Equal("Xc8b0c811918d", preview.Rows[4].Sku);   // Silver Swan no barcode1
        Assert.Equal("X1d685cfa4fca", preview.Rows[5].Sku);   // Kopiko no barcode1
        Assert.Equal("X1d685cfa4fca", preview.Rows[6].Sku);   // duplicate derived SKU -> same
        Assert.Equal("4806525912394", preview.Rows[7].Sku);   // quote-char row (Binder Clip 1")
        Assert.Equal("4806515910543", preview.Rows[8].Sku);   // barcode1 collision with row 0

        // Per-row errors are specific.
        AssertError(preview.Rows[3], "IMPORT.RETAIL_EMPTY");
        AssertError(preview.Rows[6], "IMPORT.SKU_DUPLICATE");
        AssertError(preview.Rows[8], "IMPORT.SKU_DUPLICATE");
        AssertError(preview.Rows[9], "IMPORT.MONEY_INVALID");
        AssertError(preview.Rows[10], "IMPORT.DATE_INVALID");
        AssertError(preview.Rows[11], "IMPORT.NAME_EMPTY");
        AssertError(preview.Rows[19], "IMPORT.MONEY_INVALID");

        // Quote-row name keeps the inch-mark char.
        Assert.Equal("Binder Clip 1\" Black", preview.Rows[7].Name);

        // Negative stock_qty is informational, never an error.
        Assert.Equal("-21.00", preview.Rows[1].StockQty);
        Assert.True(preview.Rows[1].StockQtyNonPositive);
        Assert.True(preview.Rows[1].IsValid);
    }

    [Fact]
    public async Task Fixture_Import_WritesCatalogRows_AndReimportIsIdempotent()
    {
        using ServiceProvider host = NewMigratedHost(NewRoot(), out ProductImportService service, out ProductStore store);

        ImportCommitResult first = await service.ImportAsync(FixturePath);
        Assert.Equal(15, first.Inserted);
        Assert.Equal(0, first.Updated);

        IReadOnlyList<Product> catalog = await store.GetAllAsync();
        Assert.Equal(15, catalog.Count); // error rows never written; no duplicates

        // Spot-check mapped rows by SKU.
        Product ace = catalog.Single(p => p.Sku == "4806515910543");
        Assert.Equal(new Money(3900), ace.RetailPrice);
        Assert.Equal(new Money(3900), ace.WholesalePrice);
        Assert.Equal("Canned Juices", ace.Category.Name);
        Assert.Equal("piece", ace.Unit);

        Product skyFlakes = catalog.Single(p => p.Sku == "4806515910544");
        Assert.Null(skyFlakes.WholesalePrice); // wholesale 0 => null (locked)

        Product sweetened = catalog.Single(p => p.Sku == "Xbd0422edfd55");
        Assert.Equal(new Money(3500), sweetened.RetailPrice); // stock_qty -21 is informational, never stored

        Product cdo = catalog.Single(p => p.Sku == "4806515910555");
        Assert.True(cdo.HasExpiry);
        Assert.Equal(new DateOnly(2026, 10, 5), cdo.ExpirationDate);

        Product magnolia = catalog.Single(p => p.Sku == "4806515910556");
        Assert.True(magnolia.SupportsPwdDiscount);
        Assert.False(magnolia.SupportsSeniorDiscount);

        Product kopikoCoffee = catalog.Single(p => p.Sku == "4806515910557");
        Assert.True(kopikoCoffee.SupportsSeniorDiscount);

        // barcode2 is dormant: the Big Datu Puti row keeps only barcode1.
        Product bigVinegar = catalog.Single(p => p.Sku == "4806515910560");
        Assert.Equal("4806515910560", bigVinegar.Barcode);

        // Re-import the SAME file: upsert-by-SKU, no duplicate rows (locked
        // decision 5 — idempotent re-import).
        ImportCommitResult second = await service.ImportAsync(FixturePath);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(15, second.Updated);
        Assert.Equal(15, (await store.GetAllAsync()).Count);

        // And the golden catalog row count matches the fixture's valid rows.
        Assert.Equal(first.Preview.Summary.ValidRows, catalog.Count);
    }

    [Fact]
    public async Task UpsertBySku_UpdatesInPlace_KeepsStableId()
    {
        using ServiceProvider host = NewMigratedHost(NewRoot(), out _, out ProductStore store);

        var original = MakeProduct("4806000000001", "Original Name", new Money(1000), new Money(900));
        Assert.True(await store.UpsertBySkuAsync(original));

        var revised = MakeProduct("4806000000001", "Revised Name", new Money(1200), null);
        Assert.False(await store.UpsertBySkuAsync(revised));

        IReadOnlyList<Product> all = await store.GetAllAsync();
        Product row = Assert.Single(all);
        Assert.Equal(original.Id, row.Id);          // stable identity preserved
        Assert.Equal("Revised Name", row.Name);
        Assert.Equal(new Money(1200), row.RetailPrice);
        Assert.Null(row.WholesalePrice);            // updated in place
    }

    private static Product MakeProduct(string sku, string name, Money retail, Money? wholesale) =>
        new(
            Id: new ProductId(Guid.NewGuid()),
            Sku: sku,
            Barcode: sku,
            Name: name,
            Description: name,
            Category: new Category("Canned Goods"),
            Unit: "piece",
            RetailPrice: retail,
            WholesalePrice: wholesale,
            Cost: null,
            HasExpiry: false,
            ExpirationDate: null,
            SupportsPwdDiscount: false,
            SupportsSeniorDiscount: false);

    private static void AssertError(ProductImportRow row, string code)
    {
        Assert.False(row.IsValid);
        Assert.Contains(row.Errors, e => e.Code == code);
    }
}
