using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Application.Contracts;

/// <summary>
/// BG-006 caller surface: the checkout session, the receipt-number source,
/// the transaction-history query service, and product search. The mutation
/// seam (ICheckoutService / ILedgerStore) is BYTE-UNCHANGED — the session,
/// query, and search services are additive over it (bg-006-arch.md).
/// </summary>

public enum CheckoutSessionState
{
    Building,
    Submitting,
    Saved,
}

/// <summary>A payment the session assembles (UI-neutral; the dialog produces
/// one draft per payment line; split = one draft per funded method).</summary>
public sealed record PaymentLineDraft(PaymentMethod Method, Money Amount, string? Reference);

public sealed record SessionLine(
    SaleLineId LineId, ProductId ProductId, string Sku, string ProductName,
    Money UnitPrice, Quantity Quantity, Money Discount, DiscountType DiscountType);

public sealed record SavedPaymentLine(PaymentMethod Method, Money Amount, string? Reference);

/// <summary>
/// The saved-sale contract the UI renders BEFORE any print attempt (D7).
/// Printing is BG-007: PrintStatus is always Pending in BG-006. Built by the
/// pure projection — never by scattered VM formatting.
/// </summary>
public sealed record SavedSaleState(
    string ReceiptNumber, SaleId SaleId, Money Total,
    IReadOnlyList<SavedPaymentLine> Payments,
    PrintStatus PrintStatus, DateTimeOffset SavedUtc, bool Replayed)
{
    public static SavedSaleState From(Sale sale, bool replayed) => new(
        sale.ReceiptNumber,
        sale.Id,
        sale.Total,
        sale.Payments
            .Select(p => new SavedPaymentLine(p.Method, p.Amount,
                string.IsNullOrWhiteSpace(p.Reference) ? null : p.Reference))
            .ToList(),
        PrintStatus.Pending,
        sale.CreatedUtc,
        replayed);
}

/// <summary>
/// The deep module (D1). One instance per sale; exactly-once is this
/// object's ownership contract: <see cref="Key"/> is minted once and reused
/// on EVERY attempt; Submit no-ops while Submitting and replays after Saved.
/// New Sale = new session.
/// </summary>
public interface ICheckoutSession
{
    CheckoutKey Key { get; }
    CheckoutSessionState State { get; }
    DayKey Day { get; }
    IReadOnlyList<SessionLine> Lines { get; }
    Money Total { get; }
    Money DiscountTotal { get; }
    string ReceiptNumber { get; }

    /// <summary>Raised after every state/line/payment change so the VM can
    /// rebind its ObservableCollection mirror (single-threaded dispatcher
    /// invariant — documented in bg-006-arch.md tradeoffs).</summary>
    event EventHandler? Changed;

    /// <summary>Adds a product (repeated scan of the same product → one line,
    /// quantity +1). Refuses when the cart quantity exceeds live on-hand
    /// (STOCK_INSUFFICIENT).</summary>
    Result<bool> AddProduct(Product product, Quantity quantity);

    /// <summary>Quantity delta with a 1 minimum and a live stock-limit
    /// re-check.</summary>
    Result<bool> AdjustQuantity(SaleLineId lineId, int delta);

    Result<bool> RemoveLine(SaleLineId lineId);

    /// <summary>Applies one permitted discount type per line (F7 cycles
    /// None → Pwd → Senior → None); DISCOUNT_NOT_ALLOWED for un-flagged
    /// products.</summary>
    Result<bool> ApplyDiscount(SaleLineId lineId, DiscountType type);

    /// <summary>Assembly incl. split and change: validates payment totals
    /// before submit; the builder re-checks in-transaction.</summary>
    Result<bool> SetPayment(IReadOnlyList<PaymentLineDraft> drafts, Money change);

    /// <summary>Exactly-once submit: same key on retry → store replay; a
    /// failure returns to Building with cart and payments intact (lane 9).</summary>
    Task<Result<SavedSaleState>> SubmitAsync(CancellationToken ct = default);
}

/// <summary>
/// Session constructor: resolves the session actor (F8/BG-005d) —
/// AUTH_ACTOR_NOT_SIGNED_IN / AUTH_ACTOR_UNKNOWN / AUTH_ACTOR_DEACTIVATED —
/// and mints the CheckoutKey once. The trading day is derived from the clock
/// (Asia/Manila); all stock lives at the single warehouse location.
/// </summary>
public interface ICheckoutSessionFactory
{
    Task<Result<ICheckoutSession>> CreateSessionAsync(CancellationToken ct = default);
}

/// <summary>
/// Receipt-number source (D6). Format R-{yyyyMMdd}-{NNN} zero-padded, Manila
/// day; overridable so scenario/lane/tests stay deterministic (the scenario
/// hard-codes R-000001..4 and bypasses the session).
/// </summary>
public interface ISaleNumberSource
{
    string Next(DateTimeOffset nowUtc, IReadOnlyCollection<string> alreadyAttempted);
}

public sealed record SaleHistoryFilter(
    DateOnly? Day, DayKey? DayKey, PaymentMethod? Method, SaleStatus? Status,
    string? Receipt, string? Reference);

public sealed record SaleHistoryRow(
    SaleId Id, string ReceiptNumber, DateTimeOffset CreatedUtc,
    DayKey Day, LocationId Location,
    Money Total, SaleStatus Status,
    IReadOnlyList<SavedPaymentLine> Payments, PrintStatus PrintStatus);

public sealed record SaleLineDetail(
    SaleLineId Id, ProductId ProductId, string Sku, string ProductName,
    Money UnitPrice, Quantity Quantity, Money Discount, Money LineTotal);

public sealed record SaleDetail(
    SaleId Id, string ReceiptNumber, DayKey Day, LocationId Location,
    DateTimeOffset CreatedUtc, UserId CreatedBy, Money Total, SaleStatus Status,
    IReadOnlyList<SaleLineDetail> Lines, IReadOnlyList<SavedPaymentLine> Payments,
    PrintStatus PrintStatus);

/// <summary>
/// Read-only transaction history (lane 10, docs/03 page 8). Pure folds over
/// store.Bind() + Context/State/Audits — no ILedgerStore extension, no
/// in-memory-mirror churn, restart/rebuild identical for free (D9).
/// </summary>
public interface ISaleQueryService
{
    Result<IReadOnlyList<SaleHistoryRow>> Query(SaleHistoryFilter? filter = null);

    Result<SaleDetail> GetByReceiptNumber(string receiptNumber);

    /// <summary>Lane 5: bank-reference search — any payment line whose
    /// reference matches (contains, OrdinalIgnoreCase).</summary>
    Result<SaleDetail> GetByPaymentReference(string reference);

    Result<SaleDetail> GetById(SaleId saleId);
}

public sealed record ProductHit(Product Product, Quantity OnHand);

/// <summary>
/// Offline product lookup (perf lane). Real EF query over indexed columns —
/// exact SKU → exact barcode (verbatim, leading zeros significant) → name
/// infix (NOCASE, wildcard-escaped, capped). The keyboard-HID scanner CR
/// suffix is trimmed at this boundary. On-hand comes from the movement fold
/// for the session actor's peddler (never a stored stock column).
/// </summary>
public interface IProductSearchService
{
    Task<Result<IReadOnlyList<ProductHit>>> SearchAsync(string query, int limit = 25, CancellationToken ct = default);
}
