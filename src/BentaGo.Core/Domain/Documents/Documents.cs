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

/// <summary>
/// Opens a trading day: records the opening cash float in integer centavos
/// (docs/decisions/0004 §2). One store, one open day at a time.
/// </summary>
public sealed record DayOpen(
    DayId Id,
    DayKey Day,
    long OpeningFloatCentavos,
    UserId By,
    DateTimeOffset WhenUtc);

/// <summary>
/// The verdict label of a day close: how the counted drawer compared to the
/// expected cash (docs/decisions/0004 §2).
/// </summary>
public enum DayBalanceLabel
{
    Balanced,
    Shortage,
    Overage,
}

/// <summary>
/// Closes a trading day: expected cash (opening float + cash sales − cash
/// refunds − approved cash expenses + cash pay-downs received), the counted
/// drawer, the signed difference, and the verdict label. The close is an
/// immutable fact — a later reopen never erases it; the prior close remains
/// visible as history (docs/decisions/0004 §6).
/// </summary>
public sealed record DayClose(
    CloseId Id,
    DayKey Day,
    long ExpectedCashCentavos,
    long CountedCashCentavos,
    long DifferenceCentavos,
    DayBalanceLabel Label,
    string? Notes,
    UserId By,
    DateTimeOffset WhenUtc);

/// <summary>
/// Reopens a closed day with an authorized reason and approver. The closed
/// day's recorded variance stands as history; corrections after the reopen
/// attach to the now-open day.
/// </summary>
public sealed record DayReopen(
    DayId Id,
    DayKey Day,
    string Reason,
    UserId ApprovedBy,
    UserId By,
    DateTimeOffset WhenUtc);

/// <summary>The day lifecycle states: NEW (no open yet) | Open | Closed.</summary>
public enum DayLifecycleState
{
    New,
    Open,
    Closed,
}

public sealed record Sale(
    SaleId Id,
    DayKey Day,
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
        DayKey day,
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

        return new Sale(id, day, location, receiptNumber, createdUtc, createdBy, lines, payments, total);
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

/// <summary>
/// A goods receipt: stock delivered into the single store warehouse. Every
/// line becomes a <see cref="Ledger.GoodsReceived"/> movement
/// (docs/decisions/0004 §5).
/// </summary>
public sealed record GoodsReceipt(
    GoodsId Id,
    DayKey Day,
    IReadOnlyList<GoodsReceiptLine> Lines,
    UserId By,
    DateTimeOffset WhenUtc);

public sealed record GoodsReceiptLine(ProductId ProductId, string Sku, string ProductName, Quantity Quantity, Money UnitCost);

/// <summary>
/// A fund entry outside a sale: a cash pay-down received (<see cref="MovementKind.FundIn"/>)
/// or an approved cash expense paid out (<see cref="MovementKind.FundOut"/>) —
/// first-class documents, not day-close text (docs/decisions/0004 §3-4).
/// </summary>
public sealed record FundEntry(
    FundEntryId Id,
    DayKey Day,
    MovementKind Direction,
    PaymentMethod Method,
    Money Amount,
    string Reason,
    UserId By,
    DateTimeOffset WhenUtc,
    UserId? ApprovedBy = null);

public sealed record Adjustment(
    AdjustmentId Id,
    DayKey Day,
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
