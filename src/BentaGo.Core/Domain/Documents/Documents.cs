using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Documents;

/// <summary>
/// Immutable fact snapshots. A document records what was agreed (commercial
/// data, line prices, totals) and groups the movements that made it happen
/// through a typed <see cref="Ledger.DocumentRef"/>. Status is never stored
/// here — it is derived by the ledger fold, so there is exactly one source
/// of truth per invariant.
/// </summary>

public sealed record Trip(TripId Id, PeddlerId Peddler, LocationId Location, DateTimeOffset CreatedUtc, UserId CreatedBy);

public sealed record Sale(
    SaleId Id,
    TripId Trip,
    PeddlerId Peddler,
    LocationId Location,
    string ReceiptNumber,
    DateTimeOffset CreatedUtc,
    UserId CreatedBy,
    IReadOnlyList<SaleLine> Lines,
    IReadOnlyList<PaymentLine> Payments,
    Money Total)
{
    public string Sku => Lines.Count == 1 ? Lines[0].Sku : "MULTI";

    /// <summary>
    /// Builds a sale and computes the commercial total exactly once, in
    /// integer centavos: Σ(unit × qty − discount) per line.
    /// </summary>
    public static Sale Create(
        SaleId id,
        TripId trip,
        PeddlerId peddler,
        LocationId location,
        string receiptNumber,
        DateTimeOffset createdUtc,
        UserId createdBy,
        IReadOnlyList<SaleLine> lines,
        IReadOnlyList<PaymentLine> payments)
    {
        Money total = Money.Zero;
        foreach (SaleLine line in lines)
        {
            total = total.Plus(line.LineTotal);
        }

        return new Sale(id, trip, peddler, location, receiptNumber, createdUtc, createdBy, lines, payments, total);
    }
}

public sealed record SaleLine(
    SaleLineId Id,
    ProductId ProductId,
    string Sku,
    string ProductName,
    Money UnitPrice,
    Quantity Quantity,
    Money Discount)
{
    public Money LineTotal => UnitPrice.Times(Quantity.Units).Minus(Discount);
}

public sealed record PaymentLine(
    PaymentLineId Id,
    PaymentMethod Method,
    string Reference,
    Money Amount,
    Money Tendered,
    Money Change);

public sealed record SaleVoid(VoidId Id, SaleId OriginalSaleId, string Reason, UserId By, DateTimeOffset WhenUtc, UserId? ApprovedBy = null);

public sealed record Return(
    ReturnId Id,
    SaleId OriginalSale,
    IReadOnlyList<ReturnLine> Lines,
    UserId By,
    DateTimeOffset WhenUtc);

public sealed record ReturnLine(SaleLineId OriginalLine, Quantity Quantity, string Reason);

public sealed record StockLoad(
    LoadId Id,
    string LoadNumber,
    TripId Trip,
    IReadOnlyList<StockLoadLine> Lines,
    UserId By,
    DateTimeOffset WhenUtc);

public sealed record StockLoadLine(ProductId ProductId, string Sku, string ProductName, Quantity Quantity, Money UnitCost);

public sealed record Adjustment(
    AdjustmentId Id,
    TripId Trip,
    ProductId ProductId,
    LocationId Location,
    AdjustmentKind Kind,
    string Reason,
    UserId By,
    DateTimeOffset WhenUtc,
    UserId? ApprovedBy = null);

public sealed record PhysicalCount(
    CountId Id,
    LocationId Location,
    IReadOnlyList<CountLine> Lines,
    UserId By,
    DateTimeOffset WhenUtc);

public sealed record CountLine(ProductId ProductId, Quantity Observed, string? Notes);

public sealed record DailyClose(
    CloseId Id,
    TripId Trip,
    Money ExpectedCash,
    Money CountedCash,
    Money Variance,
    string? Notes,
    UserId By,
    DateTimeOffset WhenUtc);

/// <summary>
/// An immutable audit fact. Sequence is a database identity (BG-005a: the
/// store assigns and returns it; <c>-1</c> is the construction-time
/// "unassigned" placeholder kept by convention). Terminal/Reason capture the
/// secure-workstation context for sensitive actions (docs/01:77).
/// </summary>
public sealed record AuditEvent(
    long Sequence,
    UserId Actor,
    DateTimeOffset WhenUtc,
    string Category,
    string Message,
    Ledger.DocumentRef? Ref,
    string? Terminal = null,
    string? Reason = null);

/// <summary>
/// The saved-sale print state a receipt-result stage shows (BG-006 D7).
/// Derived by the query service from the audit stream (latest PRINT /
/// PRINT_FAILED on a <see cref="Ledger.SaleRef"/> wins); BG-006 never
/// prints, so every freshly committed sale is Pending.
/// </summary>
public enum PrintStatus
{
    Pending,
    Printed,
    Failed,
}
