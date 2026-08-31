namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// The recorded reason category of an adjustment (BG-007 corrections).
/// Kind maps 1:1 to a movement kind: Damage→Damage, Loss→Loss,
/// Increase→AdjustmentIncrease, Decrease→AdjustmentDecrease, so "record
/// damage and loss" is a distinct typed fact, not a free-text reason.
/// </summary>
public enum AdjustmentKind
{
    Increase,
    Decrease,
    Damage,
    Loss,
}

/// <summary>
/// The closed set of movement kinds. Kind identifies behavior; the sealed
/// Movement subclasses carry the payload. Adding a kind is a compile-forced
/// update of every switch on kinds/effects.
/// </summary>
public enum MovementKind
{
    GoodsReceived,       // +q at the warehouse; document: GoodsRef
    SaleIssue,           // -q at the warehouse; document: SaleRef
    SaleIssueRevoke,     // +q restore; Reverses = the SaleIssue sequence; document: VoidRef
    ReturnToStock,       // +q back; Reverses = the SaleIssue sequence; document: ReturnRef
    Damage,              // -q; document: AdjustmentRef
    Loss,                // -q; document: AdjustmentRef
    AdjustmentIncrease,  // +q; document: AdjustmentRef
    AdjustmentDecrease,  // -q; document: AdjustmentRef
    Receipt,             // fund in; document: SaleRef
    ReceiptRefund,       // fund out (cash-back on return); Reverses = the Receipt sequence; document: ReturnRef
    ReceiptRevoke,       // fund out (void); Reverses = the Receipt sequence; document: VoidRef
    FundIn,              // fund in outside a sale (pay-down received); document: FundRef
    FundOut,             // approved cash expense; document: FundRef
    CountObserved,       // physical observation, no balance effect; document: CountRef
}

/// <summary>
/// How a movement affects balances: one of the two ledger axes or none.
/// </summary>
public enum MovementEffect
{
    None,
    StockIncrease,
    StockDecrease,
    FundIncrease,
    FundDecrease,
}
