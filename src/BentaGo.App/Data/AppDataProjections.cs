using BentaGo.Core.Domain;

namespace BentaGo.App.Data;

/// <summary>Session header: greeting, trading-day identity, day status, clock.</summary>
public sealed record SessionHeader(
    string Greeting,
    string DayText,
    string DayStatus,
    string NowText);

/// <summary>One stock row for the single store warehouse: on-hand per product.</summary>
public sealed record StockRow(
    string Sku,
    string Product,
    string OnHand,
    string LastMovementText,
    bool IsLow);

/// <summary>One per-method reconciliation summary line.</summary>
public sealed record MethodSummary(string Method, string Amount);

/// <summary>One sellable offer in the POS catalog.</summary>
public sealed record ProductOffer(string Sku, string Name, Money UnitPrice, int OnHand)
{
    /// <summary>Display price (presentation formatting only).</summary>
    public string PriceText => MoneyDisplay.Peso(UnitPrice);
}

/// <summary>
/// The immutable snapshot the read-only screens bind to. Display values are
/// preformatted strings; amounts needed for arithmetic stay Money. No
/// business math happens in the shell (the folds and document totals are the
/// computation source).
/// </summary>
public sealed record AppSnapshot(
    SessionHeader Session,
    IReadOnlyList<StockRow> Stock,
    IReadOnlyList<ProductOffer> Catalog);

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
