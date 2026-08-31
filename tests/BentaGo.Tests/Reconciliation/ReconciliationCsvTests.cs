using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using Xunit;

namespace BentaGo.Tests.Reconciliation;

/// <summary>
/// BG-008 Slice A CSV export (docs/05 469): RFC-4180 escaping, invariant
/// culture, Manila date boundaries, and the transaction/reconciliation shapes.
/// </summary>
public sealed class ReconciliationCsvTests
{
    [Fact]
    public void Escape_QuotesFieldWithCommasAndDoublesQuotes()
    {
        Assert.Equal("\"a,b\"", ReconciliationCsv.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", ReconciliationCsv.Escape("say \"hi\""));
        Assert.Equal("\"line1\nline2\"", ReconciliationCsv.Escape("line1\nline2"));
        Assert.Equal("plain", ReconciliationCsv.Escape("plain"));
        Assert.Equal("", ReconciliationCsv.Escape(""));
    }

    [Fact]
    public void Transaction_QuotesSpecialChars_InLineSummary()
    {
        (Sale sale, _) = BuildSale(
            "SKU, \"A\"", "Prod, \"1\"", new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));

        string csv = ReconciliationCsv.Transaction(new[] { sale }, _ => SaleStatus.Committed);

        // Header + one row; the SKU with a comma and a quote is escaped.
        Assert.StartsWith("Date,Receipt,Lines,Methods,Amount,Status\n", csv);
        Assert.Contains("\"SKU, \"\"A\"\" x 2\"", csv);
        Assert.Contains("Cash", csv);
    }

    [Fact]
    public void Transaction_DateUsesManilaDayBoundary()
    {
        // 2026-01-15 16:30 UTC == 2026-01-16 00:30 Asia/Manila: the day rolls over.
        (Sale sale, _) = BuildSale(
            "SKU", "Prod", new DateTimeOffset(2026, 1, 15, 16, 30, 0, TimeSpan.Zero));

        string csv = ReconciliationCsv.Transaction(new[] { sale }, _ => SaleStatus.Committed);

        string[] lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("Date,Receipt,Lines,Methods,Amount,Status", lines[0]);
        Assert.StartsWith("2026-01-16,", lines[1]);
    }

    [Fact]
    public void Transaction_OrdersNewestFirst_AndShowsMethods()
    {
        (Sale older, _) = BuildSale(
            "SKU", "Prod", new DateTimeOffset(2026, 1, 15, 4, 0, 0, TimeSpan.Zero), "R-0001");
        (Sale newer, _) = BuildSale(
            "SKU", "Prod", new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero), "R-0002");

        string csv = ReconciliationCsv.Transaction(new[] { older, newer }, _ => SaleStatus.Committed);
        string[] lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("Date,Receipt,Lines,Methods,Amount,Status", lines[0]);
        Assert.StartsWith("2026-01-15,R-0002,", lines[1]); // newer first
        Assert.StartsWith("2026-01-15,R-0001,", lines[2]);
    }

    [Fact]
    public void Reconciliation_EscapesNotesAndProductName()
    {
        var report = new ReconciliationReport(
            new DayKey("2026-01-15"),
            StoreLocations.Warehouse,
            "close, note \"x\"",
            new[]
            {
                new CashVarianceRow(Fund.PhysicalCash, Money.FromPesos(100m), Money.FromPesos(90m), Money.FromPesos(10m), VarianceLabel.Shortage),
            },
            new[]
            {
                new StockVarianceRow(
                    new ProductId(Guid.NewGuid()),
                    "SKU",
                    "Prod, \"X\"",
                    new LocationId(Guid.NewGuid()),
                    10, 8, -2, Money.FromPesos(5m), Money.FromPesos(-10m),
                    "note, with \"quote\"\nnewline"),
            },
            new ReportTotals(1, Money.FromPesos(100m), 0, 0, Money.Zero, 0,
                Money.FromPesos(100m), Money.FromPesos(90m), Money.FromPesos(10m), VarianceLabel.Shortage));

        string csv = ReconciliationCsv.Reconciliation(report);

        Assert.StartsWith("Kind,Item,Expected,Counted,Variance,Value,Label,Notes\n", csv);
        Assert.Contains("Prod, \"\"X\"\"\"", csv);                      // product name escaped inside the quoted Item cell
        Assert.Contains("\"note, with \"\"quote\"\"\nnewline\"", csv);   // notes escaped
        Assert.Contains("Physical Cash", csv);
        Assert.Contains("SHORTAGE", csv);
    }

    private static (Sale Sale, DayKey Day) BuildSale(
        string sku, string name, DateTimeOffset createdUtc, string receipt = "R-0001")
    {
        var day = DayKey.FromUtc(createdUtc);
        var location = StoreLocations.Warehouse;
        var user = new UserId(Guid.NewGuid());
        var product = new ProductId(Guid.NewGuid());
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());

        Sale sale = Sale.Create(
            new SaleId(Guid.NewGuid()),
            day,
            location,
            receipt,
            createdUtc,
            user,
            new[]
            {
                new SaleLine(lineId, product, sku, name, Money.FromPesos(1m), Quantity.Of(2), Money.Zero),
            },
            new[]
            {
                new PaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(2m), Money.FromPesos(2m), Money.Zero),
            });
        return (sale, day);
    }
}
