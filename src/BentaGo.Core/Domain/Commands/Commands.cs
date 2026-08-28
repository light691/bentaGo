using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Commands;

/// <summary>
/// Input payloads for the document builders. A command is pure data; every
/// business rule lives in the builder it feeds. Fields use owned domain types
/// (Money, Quantity, branded ids) so nothing invalid can be typed in. The id
/// fields are filled by the caller (the store or the scenario's deterministic
/// id source), so re-runs are byte-identical.
/// </summary>

public sealed record NewSaleLine(
    SaleLineId LineId,
    ProductId ProductId,
    string Sku,
    string ProductName,
    Money UnitPrice,
    Quantity Quantity,
    Money Discount);

public sealed record NewPaymentLine(
    PaymentLineId Id,
    PaymentMethod Method,
    string Reference,
    Money Amount,
    Money Tendered,
    Money Change)
{
    public static NewPaymentLine Credit(PaymentLineId id, string reference, Money amount) =>
        new(id, PaymentMethod.Credit, reference, amount, Money.Zero, Money.Zero);
}

public sealed record NewSaleCommand(
    SaleId Id,
    TripId Trip,
    string ReceiptNumber,
    UserId By,
    DateTimeOffset NowUtc,
    IReadOnlyList<NewSaleLine> Lines,
    IReadOnlyList<NewPaymentLine> Payments);

public sealed record VoidCommand(
    VoidId Id,
    SaleId Sale,
    string Reason,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record ReturnLineCommand(
    SaleLineId OriginalLine,
    Quantity Quantity,
    string Reason,
    PaymentMethod RefundMethod,
    Money RefundAmount);

public sealed record ReturnCommand(
    ReturnId Id,
    SaleId OriginalSale,
    IReadOnlyList<ReturnLineCommand> Lines,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record LoadLine(
    ProductId ProductId,
    string Sku,
    string ProductName,
    Quantity Quantity,
    Money UnitCost);

public sealed record LoadCommand(
    LoadId Id,
    string LoadNumber,
    TripId Trip,
    LocationId From,
    IReadOnlyList<LoadLine> Lines,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record TransferCommand(
    TransferId Id,
    ProductId ProductId,
    LocationId From,
    LocationId To,
    Quantity Quantity,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record AdjustmentCommand(
    AdjustmentId Id,
    ProductId ProductId,
    LocationId Location,
    bool Increase,
    Quantity Quantity,
    string Reason,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record CountLineCommand(ProductId ProductId, Quantity Observed, string? Notes);

public sealed record CountCommand(
    CountId Id,
    LocationId Location,
    IReadOnlyList<CountLineCommand> Lines,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record CloseCommand(
    CloseId Id,
    TripId Trip,
    Money CountedCash,
    string? Notes,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record ReopenCommand(
    TripId Trip,
    string Reason,
    UserId By,
    DateTimeOffset NowUtc);

public sealed record NewTripCommand(
    TripId Id,
    PeddlerId Peddler,
    LocationId Location,
    UserId By,
    DateTimeOffset NowUtc);
