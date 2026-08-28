using BentaGo.Core.Common;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Builders;

/// <summary>
/// The read side a builder receives: current fold plus the document registry.
/// Every rule below is checked against this, and the store's append guard
/// re-checks each movement through <see cref="LedgerState.Accepts"/>.
/// </summary>
public sealed record LedgerContext(
    LedgerState State,
    IReadOnlyList<Trip> Trips,
    IReadOnlyList<Sale> Sales,
    IReadOnlyList<Return> Returns,
    IReadOnlyList<StockLoad> Loads,
    IReadOnlyList<SaleVoid> Voids)
{
    public Trip? FindTrip(TripId id)
    {
        foreach (Trip trip in Trips)
        {
            if (trip.Id == id)
            {
                return trip;
            }
        }

        return null;
    }

    public Sale? FindSale(SaleId id)
    {
        foreach (Sale sale in Sales)
        {
            if (sale.Id == id)
            {
                return sale;
            }
        }

        return null;
    }
}

public sealed record SaleDraft(Sale Document, IReadOnlyList<Movement> Movements);

public sealed record VoidDraft(SaleVoid Document, IReadOnlyList<Movement> Movements);

public sealed record ReturnDraft(Return Document, IReadOnlyList<Movement> Movements);

public static class SaleBuilder
{
    public static Result<SaleDraft> Build(LedgerContext ctx, NewSaleCommand cmd)
    {
        if (cmd.Lines.Count == 0)
        {
            return Result<SaleDraft>.Fail(new DomainError("SALE_EMPTY", "a sale needs at least one line"));
        }

        Trip? trip = ctx.FindTrip(cmd.Trip);
        if (trip is null)
        {
            return Result<SaleDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        TripStatus status = ctx.State.Trip(cmd.Trip).Status;
        if (status == TripStatus.Planned)
        {
            return Result<SaleDraft>.Fail(new DomainError("TRIP_NOT_OPEN", $"trip {cmd.Trip} is not open"));
        }

        if (status == TripStatus.Closed)
        {
            return Result<SaleDraft>.Fail(new DomainError("TRIP_CLOSED", $"trip {cmd.Trip} is closed"));
        }

        var errors = new List<DomainError>();
        Money total = Money.Zero;
        var lines = new List<SaleLine>(cmd.Lines.Count);
        var seenLineIds = new HashSet<SaleLineId>();

        foreach (NewSaleLine line in cmd.Lines)
        {
            if (!seenLineIds.Add(line.LineId))
            {
                errors.Add(new DomainError("DUP_LINE", $"duplicate sale line {line.LineId}", line.Sku));
                continue;
            }

            if (line.Quantity.IsZero)
            {
                errors.Add(new DomainError("LINE_ZERO", "a sale line needs a positive quantity", line.Sku));
                continue;
            }

            if (line.Discount.Centavos < 0)
            {
                errors.Add(new DomainError("DISCOUNT_NEGATIVE", "discount cannot be negative", line.Sku));
                continue;
            }

            Money lineTotal = line.UnitPrice.Times(line.Quantity.Units).Minus(line.Discount);
            if (lineTotal.Centavos < 0)
            {
                errors.Add(new DomainError("LINE_TOTAL_NEGATIVE", "line total cannot be negative", line.Sku));
                continue;
            }

            if (ctx.State.StockOnHand(line.ProductId, trip.Location).Units < line.Quantity.Units)
            {
                errors.Add(new DomainError("STOCK_INSUFFICIENT", "not enough stock on hand", line.Sku));
                continue;
            }

            total = total.Plus(lineTotal);
            lines.Add(new SaleLine(line.LineId, line.ProductId, line.Sku, line.ProductName, line.UnitPrice, line.Quantity, line.Discount));
        }

        Money paymentsTotal = Money.Zero;
        var payments = new List<PaymentLine>(cmd.Payments.Count);
        var seenPaymentIds = new HashSet<PaymentLineId>();

        foreach (NewPaymentLine payment in cmd.Payments)
        {
            if (!seenPaymentIds.Add(payment.Id))
            {
                errors.Add(new DomainError("DUP_LINE", $"duplicate payment line {payment.Id}"));
                continue;
            }

            if (payment.Amount.Centavos <= 0)
            {
                errors.Add(new DomainError("PAYMENT_NONPOSITIVE", "a payment amount must be positive", payment.Method.DisplayName()));
                continue;
            }

            if (payment.Method == PaymentMethod.Credit && string.IsNullOrWhiteSpace(payment.Reference))
            {
                errors.Add(new DomainError("CREDIT_REFERENCE_REQUIRED", "utang needs the customer reference", payment.Method.DisplayName()));
                continue;
            }

            if (payment.Method == PaymentMethod.Cash && payment.Tendered.Centavos < payment.Amount.Centavos)
            {
                errors.Add(new DomainError("TENDER_INSUFFICIENT", "cash tendered is less than the amount", payment.Method.DisplayName()));
                continue;
            }

            if (payment.Method == PaymentMethod.Cash && payment.Change.Centavos != payment.Tendered.Centavos - payment.Amount.Centavos)
            {
                errors.Add(new DomainError("CHANGE_MISMATCH", "change does not equal tendered minus amount", payment.Method.DisplayName()));
                continue;
            }

            paymentsTotal = paymentsTotal.Plus(payment.Amount);
            payments.Add(new PaymentLine(
                payment.Id, payment.Method, payment.Reference, payment.Amount, payment.Tendered, payment.Change));
        }

        if (paymentsTotal.Centavos != total.Centavos)
        {
            errors.Add(new DomainError(
                "PAYMENT_TOTAL",
                $"payments {paymentsTotal.ToPesos():0.00} do not equal the sale total {total.ToPesos():0.00}"));
        }

        if (errors.Count > 0)
        {
            return Result<SaleDraft>.Fail(errors);
        }

        var sale = Sale.Create(
            cmd.Id, cmd.Trip, trip.Peddler, trip.Location, cmd.ReceiptNumber, cmd.NowUtc, cmd.By, lines, payments);

        var movements = new List<Movement>(lines.Count + payments.Count);
        foreach (SaleLine line in lines)
        {
            movements.Add(Moves.SaleIssue(
                0, cmd.NowUtc, cmd.By, new SaleRef(sale.Id), line.ProductId, sale.Location, line.Quantity, line.Id));
        }

        foreach (PaymentLine payment in payments)
        {
            if (payment.Method == PaymentMethod.Credit)
            {
                continue; // utang posts no fund; it is receivable-only.
            }

            movements.Add(Moves.Receipt(
                0, cmd.NowUtc, cmd.By, new SaleRef(sale.Id), payment.Method, payment.Amount, payment.Id));
        }

        return Result<SaleDraft>.Ok(new SaleDraft(sale, movements));
    }
}

public static class VoidBuilder
{
    public static Result<VoidDraft> Build(LedgerContext ctx, VoidCommand cmd)
    {
        Sale? sale = ctx.FindSale(cmd.Sale);
        if (sale is null)
        {
            return Result<VoidDraft>.Fail(new DomainError("UNKNOWN_SALE", $"no sale {cmd.Sale}"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<VoidDraft>.Fail(new DomainError("REASON_REQUIRED", "voiding needs a reason"));
        }

        SaleState state = ctx.State.Sale(sale.Id);
        if (state.Status == SaleStatus.Voided)
        {
            return Result<VoidDraft>.Fail(new DomainError("SALE_ALREADY_VOIDED", $"sale {sale.Id} is already voided"));
        }

        var errors = new List<DomainError>();
        var movements = new List<Movement>(sale.Lines.Count + sale.Payments.Count);

        foreach (SaleLine line in sale.Lines)
        {
            if (ctx.State.Returned(line.Id).Units > 0)
            {
                errors.Add(new DomainError("VOID_RETURNED", "a sale with returned items cannot be voided", line.Sku));
                continue;
            }

            long? issue = ctx.State.SaleIssueSequence(sale.Id, line.Id);
            if (issue is null)
            {
                errors.Add(new DomainError("SALE_NOT_COMMITTED", "sale issue not found", line.Sku));
                continue;
            }

            movements.Add(Moves.SaleIssueRevoke(
                0, cmd.NowUtc, cmd.By, new VoidRef(cmd.Id), line.ProductId, sale.Location, line.Quantity, issue.Value));
        }

        foreach (PaymentLine payment in sale.Payments)
        {
            if (payment.Method == PaymentMethod.Credit)
            {
                continue;
            }

            long? receipt = ctx.State.ReceiptSequence(sale.Id, payment.Id);
            if (receipt is null)
            {
                errors.Add(new DomainError("SALE_NOT_COMMITTED", "receipt not found", payment.Method.DisplayName()));
                continue;
            }

            movements.Add(Moves.ReceiptRevoke(
                0, cmd.NowUtc, cmd.By, new VoidRef(cmd.Id), payment.Method, payment.Amount, payment.Id, receipt.Value));
        }

        if (errors.Count > 0)
        {
            return Result<VoidDraft>.Fail(errors);
        }

        var @void = new SaleVoid(cmd.Id, sale.Id, cmd.Reason, cmd.By, cmd.NowUtc);
        return Result<VoidDraft>.Ok(new VoidDraft(@void, movements));
    }
}

public static class ReturnBuilder
{
    public static Result<ReturnDraft> Build(LedgerContext ctx, ReturnCommand cmd)
    {
        Sale? sale = ctx.FindSale(cmd.OriginalSale);
        if (sale is null)
        {
            return Result<ReturnDraft>.Fail(new DomainError("UNKNOWN_SALE", $"no sale {cmd.OriginalSale}"));
        }

        SaleState state = ctx.State.Sale(sale.Id);
        if (state.Status == SaleStatus.Voided)
        {
            return Result<ReturnDraft>.Fail(new DomainError("SALE_VOIDED", $"sale {sale.Id} is voided"));
        }

        if (cmd.Lines.Count == 0)
        {
            return Result<ReturnDraft>.Fail(new DomainError("RETURN_EMPTY", "a return needs at least one line"));
        }

        var errors = new List<DomainError>();
        var movements = new List<Movement>(cmd.Lines.Count);
        var lines = new List<Documents.ReturnLine>(cmd.Lines.Count);

        foreach (ReturnLineCommand line in cmd.Lines)
        {
            SaleLine? original = null;
            foreach (SaleLine sl in sale.Lines)
            {
                if (sl.Id == line.OriginalLine)
                {
                    original = sl;
                    break;
                }
            }

            if (original is null)
            {
                errors.Add(new DomainError("LINE_UNKNOWN", "return line does not belong to the sale"));
                continue;
            }

            if (line.Quantity.IsZero)
            {
                errors.Add(new DomainError("LINE_ZERO", "a return line needs a positive quantity"));
                continue;
            }

            if (ctx.State.Returned(line.OriginalLine).Units + line.Quantity.Units > original.Quantity.Units)
            {
                errors.Add(new DomainError("RETURN_LIMIT", "return exceeds the sold quantity", original.Sku));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Reason))
            {
                errors.Add(new DomainError("REASON_REQUIRED", "a return needs a reason", original.Sku));
                continue;
            }

            if (line.RefundMethod == PaymentMethod.Credit)
            {
                errors.Add(new DomainError("REFUND_METHOD", "a refund cannot post to Credit", original.Sku));
                continue;
            }

            if (line.RefundAmount.Centavos < 0)
            {
                errors.Add(new DomainError("REFUND_NEGATIVE", "refund amount cannot be negative", original.Sku));
                continue;
            }

            lines.Add(new Documents.ReturnLine(line.OriginalLine, line.Quantity, line.Reason));

            long? issue = ctx.State.SaleIssueSequence(sale.Id, line.OriginalLine);
            if (issue is null)
            {
                errors.Add(new DomainError("SALE_NOT_COMMITTED", "sale issue not found", original.Sku));
                continue;
            }

            movements.Add(Moves.ReturnToStock(
                0, cmd.NowUtc, cmd.By, new ReturnRef(cmd.Id),
                original.ProductId, sale.Location, line.Quantity, line.OriginalLine, issue.Value));

            if (line.RefundAmount.Centavos > 0)
            {
                PaymentLine? sourcePayment = null;
                foreach (PaymentLine payment in sale.Payments)
                {
                    if (payment.Method == line.RefundMethod)
                    {
                        sourcePayment = payment;
                        break;
                    }
                }

                if (sourcePayment is null)
                {
                    errors.Add(new DomainError("REFUND_RECEIPT_MISSING", "no payment to refund against", original.Sku));
                    continue;
                }

                if (line.RefundAmount.Centavos > sourcePayment.Amount.Centavos)
                {
                    errors.Add(new DomainError("REFUND_LIMIT", "refund exceeds the original payment", original.Sku));
                    continue;
                }

                long? receipt = ctx.State.ReceiptSequence(sale.Id, sourcePayment.Id);
                if (receipt is null)
                {
                    errors.Add(new DomainError("REFUND_RECEIPT_MISSING", "no receipt to refund against", original.Sku));
                    continue;
                }

                movements.Add(Moves.ReceiptRefund(
                    0, cmd.NowUtc, cmd.By, new ReturnRef(cmd.Id),
                    line.RefundMethod, line.RefundAmount, sourcePayment.Id, receipt.Value));
            }
        }

        if (errors.Count > 0)
        {
            return Result<ReturnDraft>.Fail(errors);
        }

        var @return = new Return(cmd.Id, sale.Id, lines, cmd.By, cmd.NowUtc);
        return Result<ReturnDraft>.Ok(new ReturnDraft(@return, movements));
    }
}
