using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Application.Services;

/// <summary>
/// Read-only transaction history (D9). Pure folds over store.Bind() +
/// Context/State/Audits — the TripReadService/AdminReadService pattern; no
/// ILedgerStore extension, no in-memory-mirror churn, restart/rebuild
/// identical for free. Results are peddler-scoped (cross-peddler →
/// TRIP_PEDDLER_MISMATCH).
/// </summary>
public sealed class SaleQueryService(ILedgerStore store) : ISaleQueryService
{
    public Result<IReadOnlyList<SaleHistoryRow>> Query(PeddlerId peddler, SaleHistoryFilter? filter = null)
    {
        store.Bind();
        var rows = new List<SaleHistoryRow>();
        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Peddler != peddler)
            {
                continue;
            }

            if (!Matches(sale, filter))
            {
                continue;
            }

            rows.Add(ToRow(sale));
        }

        rows.Sort((a, b) =>
        {
            int byTime = b.CreatedUtc.CompareTo(a.CreatedUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.ReceiptNumber, a.ReceiptNumber);
        });
        return Result<IReadOnlyList<SaleHistoryRow>>.Ok(rows);
    }

    public Result<SaleDetail> GetByReceiptNumber(PeddlerId peddler, string receiptNumber)
    {
        store.Bind();
        foreach (Sale sale in store.Context.Sales)
        {
            if (string.Equals(sale.ReceiptNumber, receiptNumber, StringComparison.OrdinalIgnoreCase))
            {
                return DetailFor(peddler, sale);
            }
        }

        return Result<SaleDetail>.Fail(new DomainError("UNKNOWN_SALE", $"no sale {receiptNumber}"));
    }

    public Result<SaleDetail> GetByPaymentReference(PeddlerId peddler, string reference)
    {
        store.Bind();
        foreach (Sale sale in store.Context.Sales)
        {
            foreach (PaymentLine payment in sale.Payments)
            {
                if (payment.Reference.Contains(reference, StringComparison.OrdinalIgnoreCase))
                {
                    return DetailFor(peddler, sale);
                }
            }
        }

        return Result<SaleDetail>.Fail(new DomainError("UNKNOWN_SALE", $"no sale with payment reference {reference}"));
    }

    public Result<SaleDetail> GetById(PeddlerId peddler, SaleId saleId)
    {
        store.Bind();
        Sale? sale = store.Context.FindSale(saleId);
        if (sale is null)
        {
            return Result<SaleDetail>.Fail(new DomainError("UNKNOWN_SALE", $"no sale {saleId}"));
        }

        return DetailFor(peddler, sale);
    }

    private bool Matches(Sale sale, SaleHistoryFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Day is { } day &&
            DateOnly.FromDateTime(ManilaDisplay.UtcAsManila(sale.CreatedUtc).DateTime) != day)
        {
            return false;
        }

        if (filter.Trip is { } trip && sale.Trip != trip)
        {
            return false;
        }

        if (filter.Method is { } method && !sale.Payments.Any(p => p.Method == method))
        {
            return false;
        }

        if (filter.Status is { } status && store.State.Sale(sale.Id).Status != status)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(filter.Receipt) &&
            !sale.ReceiptNumber.Contains(filter.Receipt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(filter.Reference) &&
            !sale.Payments.Any(p => p.Reference.Contains(filter.Reference, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private SaleHistoryRow ToRow(Sale sale) => new(
        sale.Id, sale.ReceiptNumber, sale.CreatedUtc, sale.Peddler, sale.Trip, sale.Location,
        sale.Total, store.State.Sale(sale.Id).Status, PaymentsOf(sale), PrintStatusOf(sale.Id));

    private Result<SaleDetail> DetailFor(PeddlerId peddler, Sale sale)
    {
        if (sale.Peddler != peddler)
        {
            return Result<SaleDetail>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH",
                $"sale {sale.ReceiptNumber} belongs to peddler {sale.Peddler}, not {peddler}"));
        }

        var lines = new List<SaleLineDetail>(sale.Lines.Count);
        foreach (SaleLine line in sale.Lines)
        {
            lines.Add(new SaleLineDetail(
                line.Id, line.ProductId, line.Sku, line.ProductName,
                line.UnitPrice, line.Quantity, line.Discount, line.LineTotal));
        }

        return Result<SaleDetail>.Ok(new SaleDetail(
            sale.Id, sale.ReceiptNumber, sale.Trip, sale.Peddler, sale.Location,
            sale.CreatedUtc, sale.CreatedBy, sale.Total, store.State.Sale(sale.Id).Status,
            lines, PaymentsOf(sale), PrintStatusOf(sale.Id)));
    }

    private static IReadOnlyList<SavedPaymentLine> PaymentsOf(Sale sale) =>
        sale.Payments
            .Select(p => new SavedPaymentLine(p.Method, p.Amount,
                string.IsNullOrWhiteSpace(p.Reference) ? null : p.Reference))
            .ToList();

    /// <summary>Latest PRINT / PRINT_FAILED audit on the sale wins; none → Pending (BG-007 owns the real status).</summary>
    private PrintStatus PrintStatusOf(SaleId saleId)
    {
        PrintStatus status = PrintStatus.Pending;
        foreach (AuditEvent audit in store.Audits)
        {
            if (audit.Ref is not SaleRef saleRef || saleRef.Id != saleId)
            {
                continue;
            }

            if (audit.Category == "PRINT")
            {
                status = PrintStatus.Printed;
            }
            else if (audit.Category == "PRINT_FAILED")
            {
                status = PrintStatus.Failed;
            }
        }

        return status;
    }
}
