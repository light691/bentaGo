using System.Globalization;
using System.Text;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;

namespace BentaGo.Application.Services;

/// <summary>
/// RFC-4180 CSV export for the BG-008 reports. Pure text: no I/O, invariant
/// culture, BOM-less UTF-8 (the caller writes the bytes), Manila date
/// boundaries (<see cref="ManilaDisplay"/>). Structure follows the
/// <see cref="ReconciliationReport"/> shapes so the export is a projection of
/// the same facts the report screen renders.
/// </summary>
public static class ReconciliationCsv
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const string ReconciliationHeader = "Kind,Item,Expected,Counted,Variance,Value,Label,Notes";
    private const string TransactionHeader = "Date,Receipt,Lines,Methods,Amount,Status";

    /// <summary>
    /// Escapes one RFC-4180 field: quotes/commas/newlines/leading-or-trailing
    /// whitespace force a quoted field, and embedded quotes are doubled.
    /// </summary>
    public static string Escape(string field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        bool needsQuotes = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            || field[0] == ' '
            || field[^1] == ' ';
        if (!needsQuotes)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// Renders the reconciliation export: one stock row per product-route line
    /// plus the countable-fund cash row. Money columns use the invariant
    /// "0.00" form; unit columns use invariant integers; a missing count is an
    /// empty cell.
    /// </summary>
    public static string Reconciliation(ReconciliationReport report)
    {
        var lines = new List<string>
        {
            ReconciliationHeader,
        };

        foreach (CashVarianceRow cash in report.CashRows)
        {
            lines.Add(Row(
                "Cash",
                cash.Fund == Fund.PhysicalCash ? "Physical Cash" : cash.Fund.ToString(),
                Money(cash.Expected),
                Money(cash.Counted),
                Money(cash.Variance),
                string.Empty,
                cash.Label.ToString().ToUpperInvariant(),
                report.CloseNotes ?? string.Empty));
        }

        foreach (StockVarianceRow stock in report.StockRows)
        {
            lines.Add(Row(
                "Stock",
                $"{stock.Sku} - {stock.ProductName}",
                stock.ExpectedUnits.ToString(Invariant),
                stock.CountedUnits?.ToString(Invariant) ?? string.Empty,
                stock.QuantityVariance?.ToString(Invariant) ?? string.Empty,
                stock.ValueVariance is { } value ? Money(value) : string.Empty,
                string.Empty,
                stock.Notes ?? string.Empty));
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Renders the transaction export: one row per sale of the trip, newest
    /// first like the transactions screen. Refunds are not separate rows —
    /// they are reflected in the sale status — and credit (utang) methods are
    /// shown by name but never move cash. The date is the Manila calendar
    /// date of the sale.
    /// </summary>
    public static string Transaction(IReadOnlyList<Sale> sales, Func<Sale, SaleStatus> statusOf)
    {
        var ordered = sales
            .OrderByDescending(s => s.CreatedUtc)
            .ThenByDescending(s => s.ReceiptNumber, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>
        {
            TransactionHeader,
        };

        foreach (Sale sale in ordered)
        {
            var lineSummaries = new List<string>(sale.Lines.Count);
            foreach (SaleLine line in sale.Lines)
            {
                lineSummaries.Add($"{line.Sku} x {line.Quantity.Units.ToString(Invariant)}");
            }

            var methods = new List<string>();
            foreach (PaymentLine payment in sale.Payments)
            {
                if (!methods.Contains(payment.Method.DisplayName()))
                {
                    methods.Add(payment.Method.DisplayName());
                }
            }

            lines.Add(Row(
                ManilaDisplay.UtcAsManila(sale.CreatedUtc).ToString("yyyy-MM-dd", Invariant),
                sale.ReceiptNumber,
                string.Join("; ", lineSummaries),
                string.Join(" | ", methods),
                Money(sale.Total),
                statusOf(sale).ToString()));
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string Row(params string[] cells) =>
        string.Join(",", cells.Select(Escape));

    private static string Money(Money money) =>
        money.ToPesos().ToString("0.00", Invariant);
}
