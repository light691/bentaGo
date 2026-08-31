using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;

namespace BentaGo.Application.Services;

/// <summary>
/// BG-008 Slice A: the per-day reconciliation projection plus the CSV export
/// (store-agnostic — the <see cref="SaleQueryService"/> pattern). It is pure
/// over the ledger read surface (Bind + Context/State/Movements + the
/// registered close/count documents), with no database access, so the report
/// is byte-identical across an in-memory store, SQLite, a reopen, and a
/// rebuild.
///
/// Semantics for this slice:
///  - Expected cash is the ADR-0004 day fold (docs/decisions/0004 §1):
///    opening float + cash sales − cash refunds/revokes − approved cash
///    expenses (FundOut) + cash pay-downs received (FundIn); the operator's
///    count is the latest DayClose for the day. Variance is expected minus
///    counted.
///  - Expected stock is the fold's StockOnHand at the warehouse; the observed
///    count is the latest CountObserved for the cell. Quantity variance is
///    counted minus expected (a negative is a shortage).
///  - The valuation unit price is fold-derived (sold value over sold
///    quantity across the day's committed sales), so value variance is
///    reproducible from the ledger alone.
/// </summary>
public sealed class ReconciliationReportService(ILedgerStore store) : IReconciliationReportService
{
    public Result<ReconciliationReport> Build(DayKey day)
    {
        store.Bind();

        LocationId location = StoreLocations.Warehouse;

        Money expectedCash = new Money(store.State.ExpectedCash(day));
        DayClose? latestClose = store.Context.DayCloses
            .Where(c => c.Day == day)
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .LastOrDefault();
        Money countedCash = latestClose is null ? Money.Zero : new Money(latestClose.CountedCashCentavos);
        Money cashVariance = expectedCash.Minus(countedCash);

        var cashRows = new List<CashVarianceRow>
        {
            new(Fund.PhysicalCash, expectedCash, countedCash, cashVariance, LabelOf(cashVariance)),
        };

        var stockRows = BuildStockRows(day, location);

        ReportTotals totals = BuildTotals(day, location, expectedCash, countedCash, cashVariance);

        return Result<ReconciliationReport>.Ok(new ReconciliationReport(
            day, location, latestClose?.Notes, cashRows, stockRows, totals));
    }

    public Result<string> BuildTransactionCsv(DayKey day)
    {
        store.Bind();
        var sales = store.Context.Sales
            .Where(s => s.Day == day)
            .ToList();
        return Result<string>.Ok(ReconciliationCsv.Transaction(sales, s => store.State.Sale(s.Id).Status));
    }

    public Result<string> BuildReconciliationCsv(DayKey day)
    {
        Result<ReconciliationReport> report = Build(day);
        if (report.IsFailure)
        {
            return Result<string>.Fail(report.Errors);
        }

        return Result<string>.Ok(ReconciliationCsv.Reconciliation(report.Value));
    }

    private List<StockVarianceRow> BuildStockRows(DayKey day, LocationId location)
    {
        var products = new HashSet<ProductId>();
        var skus = new Dictionary<ProductId, string>();
        var names = new Dictionary<ProductId, string>();
        var costs = new Dictionary<ProductId, Money>();

        // Received side first (docs/decisions/0004 §5): a goods receipt on the
        // day introduces its lines (sku, name, cost) even when nothing was
        // sold — the operator counts what arrived, not just what moved.
        foreach (GoodsReceipt receipt in store.Context.GoodsReceipts)
        {
            if (receipt.Day != day)
            {
                continue;
            }

            foreach (GoodsReceiptLine line in receipt.Lines)
            {
                if (products.Add(line.ProductId))
                {
                    skus[line.ProductId] = line.Sku;
                    names[line.ProductId] = line.ProductName;
                    costs[line.ProductId] = line.UnitCost;
                }
            }
        }

        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Day != day)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (products.Add(line.ProductId))
                {
                    skus.TryAdd(line.ProductId, line.Sku);
                    names.TryAdd(line.ProductId, line.ProductName);
                }
            }
        }

        var rows = new List<StockVarianceRow>(products.Count);
        foreach (ProductId product in products.OrderBy(p => p.Value))
        {
            int expected = store.State.StockOnHand(product, location).Units;
            int? counted = WasCounted(location, product) ? store.State.ObservedUnits(product, location).Units : null;
            int? quantityVariance = counted is { } c ? c - expected : null;
            Money unitPrice = DeriveUnitPrice(day, product, costs.GetValueOrDefault(product));
            Money? valueVariance = quantityVariance is { } qv ? unitPrice.Times(qv) : null;

            rows.Add(new StockVarianceRow(
                product,
                skus[product],
                names[product],
                location,
                expected,
                counted,
                quantityVariance,
                unitPrice,
                valueVariance,
                LatestNotes(store.Context.Counts, location, product)));
        }

        return rows;
    }

    private ReportTotals BuildTotals(DayKey day, LocationId location, Money expectedCash, Money countedCash, Money cashVariance)
    {
        int sales = 0;
        int voided = 0;
        Money salesTotal = Money.Zero;
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Day != day)
            {
                continue;
            }

            if (store.State.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                voided++;
                continue;
            }

            sales++;
            salesTotal = salesTotal.Plus(sale.Total);
        }

        int returns = 0;
        foreach (Return @return in store.Context.Returns)
        {
            Sale? original = store.Context.FindSale(@return.OriginalSale);
            if (original is not null && original.Day == day)
            {
                returns++;
            }
        }

        Money refundTotal = RefundTotal(day);

        int adjustments = 0;
        foreach (Movement movement in store.Movements)
        {
            LocationId? loc = movement switch
            {
                Damage d => d.LocationId,
                Loss l => l.LocationId,
                AdjustmentIncrease a => a.LocationId,
                AdjustmentDecrease a => a.LocationId,
                _ => null,
            };

            if (loc == location)
            {
                adjustments++;
            }
        }

        return new ReportTotals(
            sales, salesTotal, voided, returns, refundTotal, adjustments,
            expectedCash, countedCash, cashVariance, LabelOf(cashVariance));
    }

    private Money RefundTotal(DayKey day)
    {
        var dayPaymentLineIds = new HashSet<PaymentLineId>();
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Day != day)
            {
                continue;
            }

            foreach (PaymentLine payment in sale.Payments)
            {
                dayPaymentLineIds.Add(payment.Id);
            }
        }

        long total = 0;
        foreach (Movement movement in store.Movements)
        {
            if (movement is ReceiptRefund refund && dayPaymentLineIds.Contains(refund.PaymentLineId))
            {
                total += refund.Amount.Centavos;
            }
        }

        return new Money(total);
    }

    /// <summary>
    /// Fold-derived unit price: the day's committed sale value over its sold
    /// quantity for the product (deterministic, integer centavos); the goods
    /// receipt's unit cost when nothing was sold; zero when the product never
    /// appeared on the day.
    /// </summary>
    private Money DeriveUnitPrice(DayKey day, ProductId product, Money? goodsCost)
    {
        long value = 0;
        int quantity = 0;
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Day != day || store.State.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (line.ProductId != product)
                {
                    continue;
                }

                value += line.LineTotal.Centavos;
                quantity += line.Quantity.Units;
            }
        }

        if (quantity > 0)
        {
            return new Money(value / quantity);
        }

        return goodsCost ?? Money.Zero;
    }

    private bool WasCounted(LocationId location, ProductId product)
    {
        foreach (PhysicalCount count in store.Context.Counts)
        {
            if (count.Location != location)
            {
                continue;
            }

            foreach (CountLine line in count.Lines)
            {
                if (line.ProductId == product)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? LatestNotes(IReadOnlyList<PhysicalCount> counts, LocationId location, ProductId product)
    {
        CountLine? match = null;
        foreach (PhysicalCount count in counts
                     .Where(c => c.Location == location)
                     .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value))
        {
            foreach (CountLine line in count.Lines)
            {
                if (line.ProductId == product)
                {
                    match = line;
                }
            }
        }

        return match?.Notes;
    }

    private static VarianceLabel LabelOf(Money variance)
    {
        if (variance.Centavos > 0)
        {
            return VarianceLabel.Shortage;
        }

        return variance.Centavos < 0 ? VarianceLabel.Overage : VarianceLabel.Balanced;
    }
}
