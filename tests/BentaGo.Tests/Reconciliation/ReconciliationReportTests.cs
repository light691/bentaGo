using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;
using BentaGo.Tests.Domain;
using Xunit;

namespace BentaGo.Tests.Reconciliation;

/// <summary>
/// BG-008 Slice A projection rules (docs/05 449-470): expected cash from the
/// trip's own physical-cash receipts, counted cash from the submitted close,
/// the signed variance + label, per-product expected vs observed stock with a
/// fold-derived value variance, and the corrections/refund rollup.
/// </summary>
public sealed class ReconciliationReportTests
{
    private static ReconciliationReport Build(LedgerHarness h) =>
        new ReconciliationReportService(h.Store).Build(h.Day).Value;

    private static void CloseCash(LedgerHarness h, Money counted, string? notes = null)
    {
        Result<DayCloseDraft> draft = DayBuilder.Close(h.Store.Context, new DayCloseCommand(
            new CloseId(Guid.NewGuid()), h.Day, counted.Centavos, notes, h.User, h.Now.AddHours(5)));
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        h.Store.RegisterDayClose(draft.Value.Document);
        h.Store.Bind();
    }

    private static void CountAt(LedgerHarness h, ProductId product, Quantity observed, string? notes = null)
    {
        Result<CountDraft> draft = CountBuilder.Build(h.Store.Context, new CountCommand(
            new CountId(Guid.NewGuid()), StoreLocations.Warehouse,
            new[] { new CountLineCommand(product, observed, notes) }, h.User, h.Now.AddHours(2)));
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors.Select(e => e.Code)));
        h.Store.RegisterCount(draft.Value.Document);
        h.Store.Bind();
        h.Store.Append(draft.Value.Movements);
    }

    [Fact]
    public void Cash_Balanced_WhenCountedEqualsExpected()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // 90.00 physical
        CloseCash(h, Money.FromPesos(90m));

        ReconciliationReport report = Build(h);

        CashVarianceRow row = report.CashRows.Single();
        Assert.Equal(Money.FromPesos(90m), row.Expected);
        Assert.Equal(Money.FromPesos(90m), row.Counted);
        Assert.Equal(Money.FromPesos(0m), row.Variance);
        Assert.Equal(VarianceLabel.Balanced, row.Label);
        Assert.Equal(VarianceLabel.Balanced, report.Totals.CashLabel);
    }

    [Fact]
    public void Cash_Shortage_IsSignedLabelAndAmount()
    {
        var h = new LedgerHarness();
        _ = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // 90.00 expected
        CloseCash(h, Money.FromPesos(80m));

        ReconciliationReport report = Build(h);
        CashVarianceRow row = report.CashRows.Single();

        Assert.Equal(Money.FromPesos(10m), row.Variance);
        Assert.Equal(VarianceLabel.Shortage, row.Label);
        Assert.Equal("10.00", report.Totals.CashVariance.ToPesos().ToString("0.00"));
    }

    [Fact]
    public void Cash_Overage_IsSignedLabelAndAmount()
    {
        var h = new LedgerHarness();
        _ = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // 90.00 expected
        CloseCash(h, Money.FromPesos(100m));

        ReconciliationReport report = Build(h);
        CashVarianceRow row = report.CashRows.Single();

        Assert.Equal(Money.FromPesos(-10m), row.Variance);
        Assert.Equal(VarianceLabel.Overage, row.Label);
    }

    [Fact]
    public void Cash_ExpectedIsPhysicalCash_ExcludesCreditAndOtherFunds()
    {
        var h = new LedgerHarness();
        _ = h.CommitSale(3, Money.FromPesos(15m), PaymentMethod.Credit, "Aling Nena"); // utang: no fund
        _ = h.CommitSale(2, Money.FromPesos(20m), PaymentMethod.Gcash);               // GCash: not physical
        CloseCash(h, Money.Zero);

        ReconciliationReport report = Build(h);
        Assert.Equal(Money.Zero, report.Totals.ExpectedCash);
        Assert.Equal(VarianceLabel.Balanced, report.Totals.CashLabel);
    }

    [Fact]
    public void Refund_ReducesExpectedCash_AndTotsRefundTotal()
    {
        var h = new LedgerHarness();
        Sale sale = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // 2 × 45 = 90.00 physical cash

        Result<ReturnDraft> returnDraft = ReturnBuilder.Build(h.Store.Context, new ReturnCommand(
            new ReturnId(Guid.NewGuid()), sale.Id,
            new[] { new ReturnLineCommand(sale.Lines[0].Id, Quantity.Of(1), "damaged", PaymentMethod.Cash, Money.FromPesos(45m)) },
            h.User, h.Now.AddHours(3)));
        Assert.True(returnDraft.IsSuccess, string.Join("; ", returnDraft.Errors.Select(e => e.Code)));
        h.Store.RegisterReturn(returnDraft.Value.Document);
        h.Store.Bind();
        h.Store.Append(returnDraft.Value.Movements);

        CloseCash(h, Money.FromPesos(45m)); // 90 - 45 refund = 45 expected
        ReconciliationReport report = Build(h);

        Assert.Equal(Money.FromPesos(45m), report.Totals.ExpectedCash);
        Assert.Equal(Money.FromPesos(45m), report.Totals.RefundTotal);
        Assert.Equal(VarianceLabel.Balanced, report.Totals.CashLabel);
    }

    [Fact]
    public void Stock_QuantityAndValueVariance_Signed()
    {
        var h = new LedgerHarness(); // 40 sardines on hand
        _ = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash); // -> 35 on hand
        CountAt(h, h.Sardines, Quantity.Of(3)); // observed 3 -> shortage of 32

        ReconciliationReport report = Build(h);
        StockVarianceRow row = report.StockRows.Single(r => r.Product == h.Sardines);

        Assert.Equal(35, row.ExpectedUnits);
        Assert.Equal(3, row.CountedUnits);
        Assert.Equal(-32, row.QuantityVariance);
        Assert.Equal(Money.FromPesos(45m), row.UnitPrice);
        Assert.Equal(Money.FromPesos(-1440m), row.ValueVariance); // -32 × 45.00
    }

    [Fact]
    public void Stock_Overage_IsPositiveVariance()
    {
        var h = new LedgerHarness(); // 40 on hand
        _ = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash); // -> 35
        CountAt(h, h.Sardines, Quantity.Of(40)); // observed 40 -> overage of 5

        ReconciliationReport report = Build(h);
        StockVarianceRow row = report.StockRows.Single(r => r.Product == h.Sardines);

        Assert.Equal(5, row.QuantityVariance);
        Assert.Equal(Money.FromPesos(225m), row.ValueVariance); // 5 × 45.00
    }

    [Fact]
    public void Stock_Uncounted_LeavesVarianceAndValueBlank()
    {
        var h = new LedgerHarness();
        _ = h.CommitSale(5, Money.FromPesos(45m), PaymentMethod.Cash); // 35 on hand, never counted

        ReconciliationReport report = Build(h);
        StockVarianceRow row = report.StockRows.Single(r => r.Product == h.Sardines);

        Assert.Equal(35, row.ExpectedUnits);
        Assert.Null(row.CountedUnits);
        Assert.Null(row.QuantityVariance);
        Assert.Null(row.ValueVariance);
    }

    [Fact]
    public void Notes_SurfaceFromCloseAndCountDocuments()
    {
        var h = new LedgerHarness();
        _ = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash);
        CountAt(h, h.Sardines, Quantity.Of(10), "two cans dented");

        ReconciliationReport report = Build(h);
        StockVarianceRow row = report.StockRows.Single(r => r.Product == h.Sardines);
        Assert.Equal("two cans dented", row.Notes);
    }

    [Fact]
    public void ReportTotals_EqualFoldAndSaleQueryRows()
    {
        var h = new LedgerHarness();
        Sale sale1 = h.CommitSale(2, Money.FromPesos(45m), PaymentMethod.Cash); // 90.00
        Sale sale2 = h.CommitSale(1, Money.FromPesos(10m), PaymentMethod.Gcash); // 10.00 gcash
        _ = sale1;
        _ = sale2;

        // A committed, a voided (net zero), and the report cross-checks.
        Sale voidable = h.CommitSale(1, Money.FromPesos(20m), PaymentMethod.Cash);
        Result<VoidDraft> voidDraft = VoidBuilder.Build(h.Store.Context, new VoidCommand(
            new VoidId(Guid.NewGuid()), voidable.Id, "changed mind", h.User, h.Now.AddHours(2), ApprovedBy: h.User));
        Assert.True(voidDraft.IsSuccess, string.Join("; ", voidDraft.Errors.Select(e => e.Code)));
        h.Store.RegisterVoid(voidDraft.Value.Document);
        h.Store.Bind();
        h.Store.Append(voidDraft.Value.Movements);

        var query = new SaleQueryService(h.Store);
        IReadOnlyList<SaleHistoryRow> rows = query.Query(new SaleHistoryFilter(null, h.Day, null, null, null, null)).Value
            .Where(r => r.Day == h.Day)
            .ToList();
        int committed = rows.Count(r => r.Status != SaleStatus.Voided);
        Money committedTotal = rows
            .Where(r => r.Status != SaleStatus.Voided)
            .Aggregate(Money.Zero, (acc, r) => acc.Plus(r.Total));

        ReconciliationReport report = Build(h);

        Assert.Equal(committed, report.Totals.Sales);
        Assert.Equal(committedTotal, report.Totals.SalesTotal);
        Assert.Equal(1, report.Totals.Voided);
        Assert.Equal(h.Store.State.FundsOnHand(Fund.PhysicalCash), report.Totals.ExpectedCash);
    }
}
