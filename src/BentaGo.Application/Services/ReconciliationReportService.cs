using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;

namespace BentaGo.Application.Services;

/// <summary>
/// BG-008 Slice A: the per-trip reconciliation projection plus the CSV export
/// (legacy, store-agnostic — the <see cref="TripReadService"/> /
/// <see cref="SaleQueryService"/> pattern). It is pure over the ledger read
/// surface (Bind + Context/State/Movements + the registered close/count
/// documents), with no database access, so the report is byte-identical
/// across an in-memory store, SQLite, a reopen, and a rebuild.
///
/// Semantics for this slice:
///  - Expected cash is the trip's folded <see cref="Fund.PhysicalCash"/>
///    (CloseBuilder's authority); the operator's count is the latest
///    DailyClose for the trip. Variance is expected minus counted.
///  - Expected stock is the fold's <c>InventoryBreakdown.Ending</c>; the
///    observed count is the latest CountObserved for the cell. Quantity
///    variance is counted minus expected (a negative is a shortage).
///  - The valuation unit price is fold-derived (sold value over sold
///    quantity across the trip's committed sales, falling back to the load
///    unit cost), so value variance is reproducible from the ledger alone.
/// </summary>
public sealed class ReconciliationReportService(ILedgerStore store) : IReconciliationReportService
{
    public Result<ReconciliationReport> Build(PeddlerId peddler, TripId trip)
    {
        store.Bind();

        Trip? tripDoc = store.Context.FindTrip(trip);
        if (tripDoc is null)
        {
            return Result<ReconciliationReport>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {trip}"));
        }

        if (tripDoc.Peddler != peddler)
        {
            return Result<ReconciliationReport>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH", $"trip {trip} belongs to peddler {tripDoc.Peddler}, not {peddler}"));
        }

        LocationId location = tripDoc.Location;

        Money expectedCash = store.State.TripCash(trip, Fund.PhysicalCash);
        DailyClose? latestClose = store.Context.Closes
            .Where(c => c.Trip == trip)
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .LastOrDefault();
        Money countedCash = latestClose?.CountedCash ?? Money.Zero;
        Money cashVariance = expectedCash.Minus(countedCash);

        var cashRows = new List<CashVarianceRow>
        {
            new(Fund.PhysicalCash, expectedCash, countedCash, cashVariance, LabelOf(cashVariance)),
        };

        var stockRows = BuildStockRows(trip, location);

        ReportTotals totals = BuildTotals(trip, location, expectedCash, countedCash, cashVariance);

        return Result<ReconciliationReport>.Ok(new ReconciliationReport(
            trip, peddler, location, latestClose?.Notes, cashRows, stockRows, totals));
    }

    public Result<string> BuildTransactionCsv(PeddlerId peddler, TripId trip)
    {
        store.Bind();
        Result<Trip> tripResult = ValidateTrip(peddler, trip);
        if (tripResult.IsFailure)
        {
            return Result<string>.Fail(tripResult.Errors);
        }

        var sales = store.Context.Sales
            .Where(s => s.Trip == trip)
            .ToList();
        return Result<string>.Ok(ReconciliationCsv.Transaction(sales, s => store.State.Sale(s.Id).Status));
    }

    public Result<string> BuildReconciliationCsv(PeddlerId peddler, TripId trip)
    {
        Result<ReconciliationReport> report = Build(peddler, trip);
        if (report.IsFailure)
        {
            return Result<string>.Fail(report.Errors);
        }

        return Result<string>.Ok(ReconciliationCsv.Reconciliation(report.Value));
    }

    private Result<Trip> ValidateTrip(PeddlerId peddler, TripId trip)
    {
        Trip? tripDoc = store.Context.FindTrip(trip);
        if (tripDoc is null)
        {
            return Result<Trip>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {trip}"));
        }

        if (tripDoc.Peddler != peddler)
        {
            return Result<Trip>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH", $"trip {trip} belongs to peddler {tripDoc.Peddler}, not {peddler}"));
        }

        return Result<Trip>.Ok(tripDoc);
    }

    private List<StockVarianceRow> BuildStockRows(TripId trip, LocationId location)
    {
        var products = new HashSet<ProductId>();
        var skus = new Dictionary<ProductId, string>();
        var names = new Dictionary<ProductId, string>();

        foreach (StockLoad load in store.Context.Loads)
        {
            if (load.Trip != trip)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                products.Add(line.ProductId);
                skus.TryAdd(line.ProductId, line.Sku);
                names.TryAdd(line.ProductId, line.ProductName);
            }
        }

        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Trip != trip)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                products.Add(line.ProductId);
                skus.TryAdd(line.ProductId, line.Sku);
                names.TryAdd(line.ProductId, line.ProductName);
            }
        }

        var rows = new List<StockVarianceRow>(products.Count);
        foreach (ProductId product in products.OrderBy(p => p.Value))
        {
            InventoryBreakdown breakdown = store.State.Breakdown(product, location);
            int expected = (int)breakdown.Ending;
            int? counted = breakdown.Observed?.Units;
            int? quantityVariance = counted is { } c ? c - expected : null;
            Money unitPrice = DeriveUnitPrice(trip, product);
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

    private ReportTotals BuildTotals(TripId trip, LocationId location, Money expectedCash, Money countedCash, Money cashVariance)
    {
        int sales = 0;
        int voided = 0;
        Money salesTotal = Money.Zero;
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Trip != trip)
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
            if (original is not null && original.Trip == trip)
            {
                returns++;
            }
        }

        Money refundTotal = RefundTotal(trip);

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

    private Money RefundTotal(TripId trip)
    {
        var tripPaymentLineIds = new HashSet<PaymentLineId>();
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Trip != trip)
            {
                continue;
            }

            foreach (PaymentLine payment in sale.Payments)
            {
                tripPaymentLineIds.Add(payment.Id);
            }
        }

        long total = 0;
        foreach (Movement movement in store.Movements)
        {
            if (movement is ReceiptRefund refund && tripPaymentLineIds.Contains(refund.PaymentLineId))
            {
                total += refund.Amount.Centavos;
            }
        }

        return new Money(total);
    }

    /// <summary>
    /// Fold-derived unit price: the trip's committed sale value over its sold
    /// quantity for the product (deterministic, integer centavos); when nothing
    /// was sold, the last load unit cost; otherwise zero.
    /// </summary>
    private Money DeriveUnitPrice(TripId trip, ProductId product)
    {
        long value = 0;
        int quantity = 0;
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Trip != trip || store.State.Sale(sale.Id).Status == SaleStatus.Voided)
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

        Money? cost = null;
        foreach (StockLoad load in store.Context.Loads)
        {
            if (load.Trip != trip)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                if (line.ProductId == product)
                {
                    cost = line.UnitCost;
                }
            }
        }

        return cost ?? Money.Zero;
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
