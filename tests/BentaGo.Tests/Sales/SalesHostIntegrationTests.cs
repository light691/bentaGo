using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

/// <summary>
/// BG-006a infra evidence: the five BG-006 singletons resolve from the host
/// DI container (BentaGoHost registrations) and drive one durable sale
/// end-to-end — create session, search-hit add, split payment, submit,
/// then a SECOND store over the same root proves the sale hit disk.
/// </summary>
public sealed class SalesHostIntegrationTests : SalesTestBase
{
    [Fact]
    public async Task HostResolved_Factory_Search_Query_DriveOneSale_Durable()
    {
        (SalesHost host, TripId trip, PeddlerId peddler, UserId user, ProductId productId, Product product) =
            await SeededAsync();
        host.Actors.Set(user);

        // Resolve everything from DI — the registrations must exist.
        var factory = host.Provider.GetRequiredService<ICheckoutSessionFactory>();
        var search = host.Provider.GetRequiredService<IProductSearchService>();
        var query = host.Provider.GetRequiredService<ISaleQueryService>();
        var numbers = host.Provider.GetRequiredService<ISaleNumberSource>();
        Assert.IsType<CommittedSaleNumberSource>(numbers);

        Result<ICheckoutSession> created = await factory.CreateSessionAsync();
        Assert.True(created.IsSuccess, created.Errors.FirstOrDefault()?.Code ?? "create failed");
        ICheckoutSession session = created.Value!;
        Assert.Equal(trip, session.Trip);
        Assert.Equal(peddler, session.Peddler);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync(Sku);
        Assert.True(hits.IsSuccess);
        ProductHit hit = Assert.Single(hits.Value);
        Assert.Equal(productId, hit.Product.Id);
        Assert.Equal(50, hit.OnHand.Units);

        Assert.True(session.AddProduct(hit.Product, Quantity.Of(2)).IsSuccess);
        Assert.True(session.SetPayment(new[]
        {
            new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null),
            new PaymentLineDraft(PaymentMethod.Gcash, Money.FromPesos(45m), "g-6a"),
        }, Money.Zero).IsSuccess);

        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");

        // History query sees the sale (same container).
        Result<SaleDetail> byReceipt = query.GetByReceiptNumber(peddler, submitted.Value.ReceiptNumber);
        Assert.True(byReceipt.IsSuccess);
        SaleLineDetail line = Assert.Single(byReceipt.Value.Lines);
        Assert.Equal(2, line.Quantity.Units);
        Assert.Equal(2, byReceipt.Value.Payments.Count);

        Result<SaleDetail> byReference = query.GetByPaymentReference(peddler, "g-6a");
        Assert.True(byReference.IsSuccess);
        Assert.Equal(byReceipt.Value.ReceiptNumber, byReference.Value.ReceiptNumber);

        // Durability: a SECOND store over the same root sees the sale.
        string root = host.Root;
        ServiceProvider second = NewHost(root);
        await second.GetRequiredService<Infrastructure.Persistence.DbInitializer>().EnsureCreatedOrMigrateAsync();
        var store2 = second.GetRequiredService<SqliteLedgerStore>();
        store2.Bind();
        Sale? durable = null;
        foreach (Sale s in store2.Context.Sales)
        {
            if (s.ReceiptNumber == submitted.Value.ReceiptNumber)
            {
                durable = s;
            }
        }

        Assert.NotNull(durable);
        Assert.Equal(90_00, durable!.Total.Centavos);
        second.Dispose();
    }

    [Fact]
    public async Task FreshRoot_MigrationHead_Is_BG006_01()
    {
        (SalesHost host, _, _, _, _, _) = await SeededAsync();
        await using var context = await host.Provider
            .GetRequiredService<IDbContextFactory<BentaGoDbContext>>().CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(7, applied.Length);
        Assert.EndsWith("BG006_01_AddSalesIndexes", applied[^1]);
    }
}
