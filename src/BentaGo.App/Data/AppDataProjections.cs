using BentaGo.Core.Domain;

namespace BentaGo.App.Data;

/// <summary>Session header: greeting, peddler, trip, connectivity, clock, urgent banner.</summary>
public sealed record SessionHeader(
    string Greeting,
    string PeddlerName,
    string TripNumber,
    bool IsOnline,
    string NowText,
    bool PreviousDateTripOpen);

/// <summary>Dashboard cards on the peddler home screen (preformatted display strings).</summary>
public sealed record DashboardSummary(
    string TodaySalesText,
    string TransactionsText,
    string CashSalesText,
    string RemainingItemsText);

/// <summary>One "My Stock" row for the peddler's route location.</summary>
public sealed record StockRow(
    string Sku,
    string Product,
    string Loaded,
    string Sold,
    string DamagedLost,
    string Returned,
    string OnHand,
    string LastMovementText,
    bool IsLow);

/// <summary>One per-method reconciliation summary line.</summary>
public sealed record MethodSummary(string Method, string Amount);

/// <summary>
/// Trip-close summary. Money values (not display strings) so the variance
/// banner can do subtraction; the view model formats for display.
/// </summary>
public sealed record ReconciliationSummary(
    IReadOnlyList<MethodSummary> ByMethod,
    Money GrossSales,
    Money Discounts,
    Money NetSales,
    Money ExpectedCash);

/// <summary>One sellable offer in the POS catalog.</summary>
public sealed record ProductOffer(string Sku, string Name, Money UnitPrice, int OnHand);

/// <summary>
/// The immutable snapshot the read-only screens bind to. Display values are
/// preformatted strings; amounts needed for arithmetic stay Money. No
/// business math happens in the shell (the folds and document totals are the
/// computation source).
/// </summary>
public sealed record AppSnapshot(
    SessionHeader Session,
    DashboardSummary Dashboard,
    IReadOnlyList<StockRow> Stock,
    IReadOnlyList<ProductOffer> Catalog,
    ReconciliationSummary Summary);

/// <summary>
/// Pure display formatting for peso amounts (docs/03 §1). Presentation
/// formatting only, never arithmetic.
/// </summary>
public static class MoneyDisplay
{
    public static string Peso(Money money) =>
        "\u20B1" + money.ToPesos().ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-PH"));

    public static string Pesos(decimal pesos) =>
        "\u20B1" + pesos.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-PH"));
}