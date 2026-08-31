using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Reconciliation;

/// <summary>
/// The verdict of a countable reconciliation: how the counted fact compares
/// to the expected fact. <see cref="Shortage"/> means the business is short
/// (counted less than expected), <see cref="Overage"/> means more was counted
/// than expected, and <see cref="Balanced"/> means they agree. The sign is
/// always applied from the amount by the builder that constructs each row, so
/// a reader never has to remember whether a given row's variance is
/// expected-minus-counted or counted-minus-expected.
/// </summary>
public enum VarianceLabel
{
    Balanced,
    Shortage,
    Overage,
}

/// <summary>
/// One countable fund's reconciliation. This slice reports <see cref="Fund.PhysicalCash"/>
/// only — the fund the operator actually counts into the drawer; wallet, bank,
/// and credit balances are not physically counted (CloseBuilder is the
/// authority, docs/05 449-461). <see cref="Expected"/> is the fold-derived
/// per-day physical cash; <see cref="Counted"/> is the operator's submitted
/// count. <see cref="Variance"/> is expected minus counted, so a positive
/// variance is a shortage and a negative one is an overage.
/// </summary>
public sealed record CashVarianceRow(
    Fund Fund,
    Money Expected,
    Money Counted,
    Money Variance,
    VarianceLabel Label);

/// <summary>
/// One product-route cell's stock reconciliation: the fold-derived expected
/// on-hand (the <see cref="InventoryBreakdown.Ending"/>), the operator's
/// observed count, the signed quantity variance (counted minus expected), and
/// the value variance at the fold-derived unit price. <c>Counted</c>, the
/// quantity variance, and the value variance are null when the product was
/// never counted on the day. <see cref="UnitPrice"/> is always a fold-derived
/// price (sold value over sold quantity, or the load cost when nothing was
/// sold), so the value variance is reproducible from the ledger alone.
/// </summary>
public sealed record StockVarianceRow(
    ProductId Product,
    string Sku,
    string ProductName,
    LocationId Location,
    int ExpectedUnits,
    int? CountedUnits,
    int? QuantityVariance,
    Money UnitPrice,
    Money? ValueVariance,
    string? Notes);

/// <summary>
/// The report-header rollup the operator reads first (docs/05 460): the
/// committed sales, refunds, and correction summary for the day, plus the
/// cash verdict. Every money field is a fold-derived integer-centavo amount.
/// </summary>
public sealed record ReportTotals(
    int Sales,
    Money SalesTotal,
    int Voided,
    int Returns,
    Money RefundTotal,
    int Adjustments,
    Money ExpectedCash,
    Money CountedCash,
    Money CashVariance,
    VarianceLabel CashLabel);

/// <summary>
/// The complete per-day reconciliation report for the store close
/// (BG-008 Slice A). It is a pure projection over the ledger read surface
/// (fold + registered documents) — never a database query — so it renders
/// byte-identically across an in-memory store, SQLite, a reopen, and a
/// rebuild. <see cref="CashRows"/> holds the countable-fund row(s);
/// <see cref="StockRows"/> holds one row per product cell the day
/// touched; <see cref="Totals"/> rolls the header up.
/// </summary>
public sealed record ReconciliationReport(
    DayKey Day,
    LocationId Location,
    string? CloseNotes,
    IReadOnlyList<CashVarianceRow> CashRows,
    IReadOnlyList<StockVarianceRow> StockRows,
    ReportTotals Totals);
