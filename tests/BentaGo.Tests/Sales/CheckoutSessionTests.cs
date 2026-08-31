using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

public class CheckoutSessionTests : SalesTestBase
{
    private static FixedNumberSource Numbers(params string[] receipts) =>
        new(new Queue<string>(receipts));

    private static Product Wide(ProductId id) => TestProduct(id, pwd: true, senior: true);

    [Fact]
    public async Task Cash_Exact_Change_Commits_One_Sale_And_Decrements_Stock()
    {
        var (host, _, user, productId, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession real = NewSessionFactory(host);

        Assert.True(real.AddProduct(product, Quantity.Of(2)).IsSuccess, "add failed");
        SessionLine line = Assert.Single(real.Lines);
        Assert.Equal(2, line.Quantity.Units);

        // 2 x 45 = 90.00; cash 90 tendered with 10.00 change.
        Assert.True(real.SetPayment(new[]
        {
            new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(90m), null),
        }, Money.FromPesos(10m)).IsSuccess, "payment failed");

        Result<SavedSaleState> submitted = await real.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");
        SavedSaleState saved = submitted.Value;
        Assert.Equal("R-T1", saved.ReceiptNumber);
        Assert.Equal("90.00", saved.Total.ToPesos().ToString("0.00"));
        Assert.False(saved.Replayed);
        Assert.Equal(PrintStatus.Pending, saved.PrintStatus);
        Assert.Single(saved.Payments);

        host.Store.Bind();
        Assert.Equal(Quantity.Of(98), host.Store.State.StockOnHand(productId,
            StoreLocations.Warehouse));
    }

    [Fact]
    public async Task Duplicate_Submit_Same_Key_Replays_One_Sale()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);
        Assert.True(session.AddProduct(product, Quantity.Of(1)).IsSuccess);
        Assert.True(session.SetPayment(new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) }, Money.Zero).IsSuccess);

        Result<SavedSaleState> first = await session.SubmitAsync();
        Assert.True(first.IsSuccess);
        Result<SavedSaleState> second = await session.SubmitAsync();
        Assert.True(second.IsSuccess);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.ReceiptNumber, second.Value.ReceiptNumber);
        Assert.Equal(first.Value.SaleId, second.Value.SaleId);

        host.Store.Bind();
        Assert.Single(host.Store.Context.Sales);
    }

    [Fact]
    public async Task Oversell_Refuses_Before_Persistence_And_Cart_Recovers()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);

        Result<bool> first = session.AddProduct(product, Quantity.Of(100));
        Assert.True(first.IsSuccess);
        Result<bool> second = session.AddProduct(product, Quantity.Of(1));
        Assert.True(second.IsFailure);
        Assert.Equal("STOCK_INSUFFICIENT", second.Errors[0].Code);
        Assert.Single(session.Lines);

        // Cart recoverable: keep 100, submit, exactly one sale.
        Assert.True(session.SetPayment(new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(4500m), null) }, Money.Zero).IsSuccess);
        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess);
        host.Store.Bind();
        Assert.Single(host.Store.Context.Sales);
        Assert.Equal(Quantity.Of(0), host.Store.State.StockOnHand(product.Id,
            StoreLocations.Warehouse));
    }

    [Fact]
    public async Task Bank_Transfer_Requires_Reference()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);
        Assert.True(session.AddProduct(product, Quantity.Of(1)).IsSuccess);

        Result<bool> bad = session.SetPayment(
            new[] { new PaymentLineDraft(PaymentMethod.BankTransfer, Money.FromPesos(45m), null) }, Money.Zero);
        Assert.True(bad.IsFailure);
        Assert.Equal("BANK_REFERENCE_REQUIRED", bad.Errors[0].Code);

        // Missing reference never persists anything.
        Result<SavedSaleState> submit = await session.SubmitAsync();
        Assert.True(submit.IsFailure);
        host.Store.Bind();
        Assert.Empty(host.Store.Context.Sales);
    }

    [Fact]
    public async Task Split_Payment_Exact_Totals_And_Funds()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);
        Assert.True(session.AddProduct(product, Quantity.Of(3)).IsSuccess); // 135.00

        Result<bool> set = session.SetPayment(new[]
        {
            new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(50m), null),
            new PaymentLineDraft(PaymentMethod.Gcash, Money.FromPesos(85m), "g-1"),
        }, Money.Zero);
        Assert.True(set.IsSuccess, set.Errors.FirstOrDefault()?.Code ?? "split failed");

        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");
        Assert.Equal(2, submitted.Value.Payments.Count);
        host.Store.Bind();
        Assert.Equal(50_00, host.Store.State.FundsOnHand(Fund.PhysicalCash).Centavos);
        Assert.Equal(85_00, host.Store.State.FundsOnHand(Fund.Gcash).Centavos);
    }

    [Fact]
    public async Task Pwd_Discount_Applies_Floor_Math()
    {
        var (host, _, user, productId, _) = await SeededAsync();
        host.Actors.Set(user);
        Product pwd = TestProduct(productId, pwd: true);
        await host.Products.UpsertBySkuAsync(pwd);
        CheckoutSession session = NewSessionFactory(host);

        Assert.True(session.AddProduct(pwd, Quantity.Of(1)).IsSuccess);
        SessionLine line = Assert.Single(session.Lines);
        Assert.True(session.ApplyDiscount(line.LineId, DiscountType.Pwd).IsSuccess);
        Assert.Equal(9_00, session.DiscountTotal.Centavos);       // 45.00 x 20%
        Assert.Equal(36_00, session.Total.Centavos);              // 36.00 after discount

        Assert.True(session.SetPayment(new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(36m), null) }, Money.Zero).IsSuccess);
        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");
        Assert.Equal(36_00, submitted.Value.Total.Centavos);
    }

    [Fact]
    public async Task Unflagged_Discount_Is_Rejected()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);
        Assert.True(session.AddProduct(product, Quantity.Of(1)).IsSuccess);
        SessionLine line = Assert.Single(session.Lines);

        Result<bool> r = session.ApplyDiscount(line.LineId, DiscountType.Pwd);
        Assert.True(r.IsFailure);
        Assert.Equal("DISCOUNT_NOT_ALLOWED", r.Errors[0].Code);
    }

    [Fact]
    public async Task Successful_Submit_Reserves_Receipt_On_First_Attempt()
    {
        var (host, _, user, _, product) = await SeededAsync();
        host.Actors.Set(user);
        CheckoutSession session = NewSessionFactory(host);
        Assert.Equal("", session.ReceiptNumber); // reserved only at first submit
        Assert.True(session.AddProduct(product, Quantity.Of(1)).IsSuccess);
        Assert.True(session.SetPayment(new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) }, Money.Zero).IsSuccess);
        Result<SavedSaleState> submitted = await session.SubmitAsync();
        Assert.True(submitted.IsSuccess);
        Assert.Equal("R-T1", submitted.Value.ReceiptNumber);
    }

    private CheckoutSession NewSessionFactory(SalesHost host)
    {
        CheckoutSessionFactory factory = NewFactory(host);
        Result<ICheckoutSession> created = factory.CreateSessionAsync().GetAwaiter().GetResult();
        Assert.True(created.IsSuccess, created.Errors.FirstOrDefault()?.Code ?? "create failed");
        return (CheckoutSession)created.Value!;
    }
}
