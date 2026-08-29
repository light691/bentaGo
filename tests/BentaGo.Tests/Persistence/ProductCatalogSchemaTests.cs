using System.IO;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// BG-005b catalog schema: the Products table carries the additive v2 product
/// contract — SKU unique (the derived stable key), barcode1 unique when
/// present, money as integer centavos, DateOnly expiry as TEXT, barcode2 and
/// stock_qty deliberately absent (dormant / movements-owned).
/// </summary>
public sealed class ProductCatalogSchemaTests : PersistenceTestBase
{
    [Fact]
    public async Task ProductsTable_HasFullV2Shape()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var info = await TableInfoAsync(DbPath(root), "Products");
        Assert.Contains("Id", info.Keys);
        Assert.Contains("Sku", info.Keys);
        Assert.Contains("Barcode", info.Keys);
        Assert.Contains("Name", info.Keys);
        Assert.Contains("Description", info.Keys);
        Assert.Contains("Category", info.Keys);
        Assert.Contains("Unit", info.Keys);
        Assert.Contains("RetailPrice", info.Keys);
        Assert.Contains("WholesalePrice", info.Keys);
        Assert.Contains("Cost", info.Keys);
        Assert.Contains("HasExpiry", info.Keys);
        Assert.Contains("ExpirationDate", info.Keys);
        Assert.Contains("SupportsPwdDiscount", info.Keys);
        Assert.Contains("SupportsSeniorDiscount", info.Keys);

        // Dormant / movements-owned columns are NOT part of the catalog.
        Assert.DoesNotContain("Barcode2", info.Keys);
        Assert.DoesNotContain("StockQty", info.Keys);

        // Money is stored as integer centavos (docs/02: integer minor units).
        Assert.Equal("INTEGER", info["RetailPrice"]);
        Assert.Equal("INTEGER", info["WholesalePrice"]);
        Assert.Equal("INTEGER", info["Cost"]);
    }

    [Fact]
    public async Task ProductsTable_SkuAndBarcode_AreUniqueIndexes()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        using var connection = new SqliteConnection($"Data Source={DbPath(root)};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"name\", \"tbl_name\" FROM \"sqlite_master\" " +
            "WHERE \"type\" = 'index' AND \"tbl_name\" = 'Products'";
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var indexes = new List<string>();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("IX_Products_Sku", indexes);
        Assert.Contains("IX_Products_Barcode", indexes);

        // UNIQUE flags: both indexes enforce uniqueness (barcode NULLs are
        // allowed multiple times in SQLite, so "unique when present" holds).
        var unique = new HashSet<string>(StringComparer.Ordinal);
        using var indexList = connection.CreateCommand();
        indexList.CommandText = "PRAGMA index_list('Products')";
        using SqliteDataReader info = await indexList.ExecuteReaderAsync();
        while (await info.ReadAsync())
        {
            if (Convert.ToInt64(info.GetValue(2)) == 1) // "unique" column
            {
                unique.Add(info.GetString(1));
            }
        }

        Assert.Contains("IX_Products_Sku", unique);
        Assert.Contains("IX_Products_Barcode", unique);
        await connection.CloseAsync();
    }

    private static async Task<Dictionary<string, string>> TableInfoAsync(string dbPath, string table)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(1)] = reader.GetString(2); // name -> type
        }

        await connection.CloseAsync();
        return result;
    }
}