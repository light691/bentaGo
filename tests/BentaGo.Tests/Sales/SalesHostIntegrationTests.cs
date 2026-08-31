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
        (SalesHost host, DayKey day, UserId user, ProductId productId, Product product) =
            await SeededAsync();
        host.Actors.Set(user);

        // The DI factory derives the trading day from the real clock (the
        // production contract), so open TODAY alongside the fixture seed day.
        DayKey today = DayKey.FromUtc(DateTimeOffset.UtcNow);
        host.Store.RegisterDayOpen(new DayOpen(new DayId(Guid.NewGuid()), today, 0, user, DateTimeOffset.UtcNow));
        host.Store.Bind();

        // Resolve everything from DI — the registrations must exist.
        var factory = host.Provider.GetRequiredService<ICheckoutSessionFactory>();
        var search = host.Provider.GetRequiredService<IProductSearchService>();
        var query = host.Provider.GetRequiredService<ISaleQueryService>();
        var numbers = host.Provider.GetRequiredService<ISaleNumberSource>();
        Assert.IsType<CommittedSaleNumberSource>(numbers);

        Result<ICheckoutSession> created = await factory.CreateSessionAsync();
        Assert.True(created.IsSuccess, created.Errors.FirstOrDefault()?.Code ?? "create failed");
        ICheckoutSession session = created.Value!;
        Assert.Equal(today, session.Day);

        Result<IReadOnlyList<ProductHit>> hits = await search.SearchAsync(Sku);
        Assert.True(hits.IsSuccess);
        ProductHit hit = Assert.Single(hits.Value);
        Assert.Equal(productId, hit.Product.Id);
        Assert.Equal(100, hit.OnHand.Units);

        Assert.True(session.AddProduct(hit.Product, Quantity.Of(2)).IsSuccess);
        Assert.True(session.SetPayment(new[]
        {
            new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null),
            new PaymentLineDraft(PaymentMethod.Gcash, Money.FromPesos(45m), "g-6a"),
        }, Money.Zero).IsSuccess);

        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");

        // History query sees the sale (same container).
        Result<SaleDetail> byReceipt = query.GetByReceiptNumber(submitted.Value.ReceiptNumber);
        Assert.True(byReceipt.IsSuccess);
        SaleLineDetail line = Assert.Single(byReceipt.Value.Lines);
        Assert.Equal(2, line.Quantity.Units);
        Assert.Equal(2, byReceipt.Value.Payments.Count);

        Result<SaleDetail> byReference = query.GetByPaymentReference("g-6a");
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
    public async Task FreshRoot_MigrationHead_Is_TheP1Baseline()
    {
        (SalesHost host, _, _, _, _) = await SeededAsync();
        await using var context = await host.Provider
            .GetRequiredService<IDbContextFactory<BentaGoDbContext>>().CreateDbContextAsync();
        string[] applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(applied);
        Assert.EndsWith("Baseline", applied[^1]);
    }
}
