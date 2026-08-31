using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

public class ProductSearchTests : SalesTestBase
{
    [Fact]
    public async Task Search_By_Sku_Id_Is_Case_Insensitive()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        var search = new ProductSearchService(host.Store, host.Products);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync("sku");
        Assert.True(hits.IsSuccess, hits.Errors.FirstOrDefault()?.Code ?? "search failed");
        ProductHit hit = Assert.Single(hits.Value);
        Assert.Equal(product.Id, hit.Product.Id);
        Assert.Equal(100, hit.OnHand.Units);
    }

    [Fact]
    public async Task Search_By_Barcode_Keeps_Leading_Zeros_Verbatim()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        var search = new ProductSearchService(host.Store, host.Products);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync(Barcode);
        Assert.True(hits.IsSuccess);
        Assert.Single(hits.Value);
        Assert.Equal(product.Id, hits.Value[0].Product.Id);

        // A barcode with the same digits as a number (no leading zeros) must
        // NOT match — barcode equality is verbatim TEXT.
        Result<IReadOnlyList<ProductHit>> wrong = await search.SearchAsync("123456789012");
        Assert.True(wrong.IsSuccess);
        Assert.Empty(wrong.Value);
    }

    [Fact]
    public async Task Search_By_Name_Infix_Returns_Hits()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        var search = new ProductSearchService(host.Store, host.Products);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync("test");
        Assert.True(hits.IsSuccess);
        Assert.Contains(hits.Value, h => h.Product.Id == product.Id);
    }

    [Fact]
    public async Task Scanner_Cr_Suffix_Is_Trimmed_At_The_Boundary()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        var search = new ProductSearchService(host.Store, host.Products);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync(Sku + "\r");
        Assert.True(hits.IsSuccess);
        Assert.Single(hits.Value);
        Assert.Equal(product.Id, hits.Value[0].Product.Id);
    }

    [Fact]
    public async Task Empty_Query_Returns_No_Hits()
    {
        var (host, _, _, _, _) = await SeededAsync();
        var search = new ProductSearchService(host.Store, host.Products);
        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync("   ");
        Assert.True(hits.IsSuccess);
        Assert.Empty(hits.Value);
    }
}
