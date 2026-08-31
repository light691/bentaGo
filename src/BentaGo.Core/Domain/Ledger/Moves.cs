using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// The single construction path for movements. Every factory enforces the
/// kind's own legality: UTC timestamps, positive quantities, no fund posting
/// for Credit, reversal targets required only where they are meaningful.
/// </summary>
public static class Moves
{
    public static GoodsReceived GoodsReceived(
        long sequence, DateTimeOffset whenUtc, UserId actor, GoodsRef document,
        ProductId productId, LocationId locationId, Quantity quantity) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
        };

    public static SaleIssue SaleIssue(
        long sequence, DateTimeOffset whenUtc, UserId actor, SaleRef document,
        ProductId productId, LocationId locationId, Quantity quantity, SaleLineId saleLineId) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            SaleLineId = saleLineId,
        };

    public static SaleIssueRevoke SaleIssueRevoke(
        long sequence, DateTimeOffset whenUtc, UserId actor, VoidRef document,
        ProductId productId, LocationId locationId, Quantity quantity, long reverses) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            Reverses = reverses,
        };

    public static ReturnToStock ReturnToStock(
        long sequence, DateTimeOffset whenUtc, UserId actor, ReturnRef document,
        ProductId productId, LocationId locationId, Quantity quantity, SaleLineId saleLineId, long reverses) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            SaleLineId = saleLineId,
            Reverses = reverses,
        };

    public static Damage Damage(
        long sequence, DateTimeOffset whenUtc, UserId actor, AdjustmentRef document,
        ProductId productId, LocationId locationId, Quantity quantity, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            Reason = Require(reason, nameof(reason)),
        };

    public static Loss Loss(
        long sequence, DateTimeOffset whenUtc, UserId actor, AdjustmentRef document,
        ProductId productId, LocationId locationId, Quantity quantity, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            Reason = Require(reason, nameof(reason)),
        };

    public static AdjustmentIncrease AdjustmentIncrease(
        long sequence, DateTimeOffset whenUtc, UserId actor, AdjustmentRef document,
        ProductId productId, LocationId locationId, Quantity quantity, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            Reason = Require(reason, nameof(reason)),
        };

    public static AdjustmentDecrease AdjustmentDecrease(
        long sequence, DateTimeOffset whenUtc, UserId actor, AdjustmentRef document,
        ProductId productId, LocationId locationId, Quantity quantity, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            Quantity = Positive(quantity),
            Reason = Require(reason, nameof(reason)),
        };

    public static Receipt Receipt(
        long sequence, DateTimeOffset whenUtc, UserId actor, SaleRef document,
        PaymentMethod method, Money amount, PaymentLineId paymentLineId) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            Method = NotCredit(method),
            Amount = Positive(amount),
            PaymentLineId = paymentLineId,
        };

    public static ReceiptRefund ReceiptRefund(
        long sequence, DateTimeOffset whenUtc, UserId actor, ReturnRef document,
        PaymentMethod method, Money amount, PaymentLineId paymentLineId, long reverses) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            Method = NotCredit(method),
            Amount = Positive(amount),
            PaymentLineId = paymentLineId,
            Reverses = reverses,
        };

    public static ReceiptRevoke ReceiptRevoke(
        long sequence, DateTimeOffset whenUtc, UserId actor, VoidRef document,
        PaymentMethod method, Money amount, PaymentLineId paymentLineId, long reverses) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            Method = NotCredit(method),
            Amount = Positive(amount),
            PaymentLineId = paymentLineId,
            Reverses = reverses,
        };

    public static FundIn FundIn(
        long sequence, DateTimeOffset whenUtc, UserId actor, FundRef document,
        PaymentMethod method, Money amount, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            Method = NotCredit(method),
            Amount = Positive(amount),
            Reason = Require(reason, nameof(reason)),
        };

    public static FundOut FundOut(
        long sequence, DateTimeOffset whenUtc, UserId actor, FundRef document,
        PaymentMethod method, Money amount, string reason) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            Method = NotCredit(method),
            Amount = Positive(amount),
            Reason = Require(reason, nameof(reason)),
        };

    public static CountObserved CountObserved(
        long sequence, DateTimeOffset whenUtc, UserId actor, CountRef document,
        ProductId productId, LocationId locationId, Quantity observedQuantity) =>
        new()
        {
            Sequence = sequence,
            WhenUtc = Utc(whenUtc),
            ActorId = actor,
            Document = document,
            ProductId = productId,
            LocationId = locationId,
            ObservedQuantity = observedQuantity,
        };

    private static DateTimeOffset Utc(DateTimeOffset whenUtc) =>
        whenUtc.Offset == TimeSpan.Zero
            ? whenUtc
            : throw new ArgumentException("movement timestamps must be UTC", nameof(whenUtc));

    private static Quantity Positive(Quantity quantity) =>
        quantity.IsZero
            ? throw new ArgumentException("stock movement quantity must be positive", nameof(quantity))
            : quantity;

    private static Money Positive(Money amount) =>
        amount.Centavos <= 0
            ? throw new ArgumentException("fund movement amount must be positive", nameof(amount))
            : amount;

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("a reason is required", name)
            : value;

    private static PaymentMethod NotCredit(PaymentMethod method) =>
        method == PaymentMethod.Credit
            ? throw new ArgumentException("Credit has no fund and cannot post a fund movement", nameof(method))
            : method;
}
