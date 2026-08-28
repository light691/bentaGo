namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// The closed set of movement kinds. Kind identifies behavior; the sealed
/// Movement subclasses carry the payload. Adding a kind is a compile-forced
/// update of every switch on kinds/effects.
/// </summary>
public enum MovementKind
{
    OpeningStock,        // +q at a location; document: LoadRef
    TransferOut,         // -q at source; document: LoadRef (loads-to-peddler) or TransferRef
    TransferIn,          // +q at destination; document: same LoadRef or TransferRef
    SaleIssue,           // -q at selling location; document: SaleRef
    SaleIssueRevoke,     // +q restore; Reverses = the SaleIssue sequence; document: VoidRef
    ReturnToStock,       // +q back; Reverses = the SaleIssue sequence; document: ReturnRef
    Damage,              // -q; document: AdjustmentRef
    Loss,                // -q; document: AdjustmentRef
    AdjustmentIncrease,  // +q; document: AdjustmentRef
    AdjustmentDecrease,  // -q; document: AdjustmentRef
    Receipt,             // fund in; document: SaleRef
    ReceiptRefund,       // fund out (cash-back on return); Reverses = the Receipt sequence; document: ReturnRef
    ReceiptRevoke,       // fund out (void); Reverses = the Receipt sequence; document: VoidRef
    CountObserved,       // physical observation, no balance effect; document: CountRef
    TripClose,           // lifecycle, no balance effect; document: CloseRef
    TripReopen,          // lifecycle, no balance effect; document: ReopenRef
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
