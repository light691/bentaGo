using BentaGo.Core.Common;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Builders;

public sealed record GoodsReceiptDraft(GoodsReceipt Document, IReadOnlyList<Movement> Movements);

public sealed record FundEntryDraft(FundEntry Document, IReadOnlyList<Movement> Movements);

public sealed record AdjustmentDraft(Adjustment Document, IReadOnlyList<Movement> Movements);

public sealed record CountDraft(PhysicalCount Document, IReadOnlyList<Movement> Movements);

public sealed record DayOpenDraft(DayOpen Document);

public sealed record DayCloseDraft(DayClose Document);

public sealed record DayReopenDraft(DayReopen Document);

/// <summary>
/// Receives stock into the single store warehouse: every line becomes a
/// <see cref="MovementKind.GoodsReceived"/> movement
/// (docs/decisions/0004 §5). Receiving is legal on an open day.
/// </summary>
public static class GoodsReceiptBuilder
{
    public static Result<GoodsReceiptDraft> Build(LedgerContext ctx, GoodsReceiptCommand cmd)
    {
        if (cmd.Lines.Count == 0)
        {
            return Result<GoodsReceiptDraft>.Fail(new DomainError("RECEIPT_EMPTY", "a goods receipt needs at least one line"));
        }

        if (ctx.State.Day(cmd.Day) != DayLifecycleState.Open)
        {
            return Result<GoodsReceiptDraft>.Fail(new DomainError(
                "DAY_NOT_OPEN", $"day {cmd.Day} is {ctx.State.Day(cmd.Day)}; receiving needs an open day"));
        }

        var errors = new List<DomainError>();
        var movements = new List<Movement>(cmd.Lines.Count);
        var lines = new List<GoodsReceiptLine>(cmd.Lines.Count);

        foreach (GoodsReceiptLineCommand line in cmd.Lines)
        {
            if (line.Quantity.IsZero)
            {
                errors.Add(new DomainError("LINE_ZERO", "a goods receipt line needs a positive quantity", line.Sku));
                continue;
            }

            lines.Add(new GoodsReceiptLine(line.ProductId, line.Sku, line.ProductName, line.Quantity, line.UnitCost));
            movements.Add(Moves.GoodsReceived(
                0, cmd.NowUtc, cmd.By, new GoodsRef(cmd.Id), line.ProductId, StoreLocations.Warehouse, line.Quantity));
        }

        if (errors.Count > 0)
        {
            return Result<GoodsReceiptDraft>.Fail(errors);
        }

        var receipt = new GoodsReceipt(cmd.Id, cmd.Day, lines, cmd.By, cmd.NowUtc);
        return Result<GoodsReceiptDraft>.Ok(new GoodsReceiptDraft(receipt, movements));
    }
}

/// <summary>
/// Fund entries outside a sale: <see cref="MovementKind.FundIn"/> (a cash
/// pay-down received on a receivable) and <see cref="MovementKind.FundOut"/>
/// (an approved cash expense) — first-class documents with a reason
/// (docs/decisions/0004 §3-4). Both are legal only on an open day.
/// </summary>
public static class FundEntryBuilder
{
    public static Result<FundEntryDraft> Build(LedgerContext ctx, FundEntryCommand cmd)
    {
        if (cmd.Direction is not (MovementKind.FundIn or MovementKind.FundOut))
        {
            return Result<FundEntryDraft>.Fail(new DomainError(
                "FUND_DIRECTION", $"a fund entry must be {MovementKind.FundIn} or {MovementKind.FundOut}"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<FundEntryDraft>.Fail(new DomainError("REASON_REQUIRED", "a fund entry needs a reason"));
        }

        if (ctx.State.Day(cmd.Day) != DayLifecycleState.Open)
        {
            return Result<FundEntryDraft>.Fail(new DomainError(
                "DAY_NOT_OPEN", $"day {cmd.Day} is {ctx.State.Day(cmd.Day)}; a fund entry needs an open day"));
        }

        // A cash expense moves funds out of the drawer without a sale, so it
        // records who approved (BG-007 approval policy).
        if (cmd.Direction == MovementKind.FundOut && cmd.ApprovedBy is null)
        {
            return Result<FundEntryDraft>.Fail(new DomainError(
                "FUND_OUT_APPROVAL_REQUIRED", "a fund out needs an operator approval"));
        }

        Movement movement = cmd.Direction == MovementKind.FundIn
            ? Moves.FundIn(0, cmd.NowUtc, cmd.By, new FundRef(cmd.Id), cmd.Method, cmd.Amount, cmd.Reason)
            : Moves.FundOut(0, cmd.NowUtc, cmd.By, new FundRef(cmd.Id), cmd.Method, cmd.Amount, cmd.Reason);

        var document = new FundEntry(
            cmd.Id, cmd.Day, cmd.Direction, cmd.Method, cmd.Amount, cmd.Reason, cmd.By, cmd.NowUtc, cmd.ApprovedBy);
        return Result<FundEntryDraft>.Ok(new FundEntryDraft(document, new[] { movement }));
    }
}

public static class AdjustmentBuilder
{
    public static Result<AdjustmentDraft> Build(LedgerContext ctx, AdjustmentCommand cmd)
    {
        if (cmd.Quantity.IsZero)
        {
            return Result<AdjustmentDraft>.Fail(new DomainError("LINE_ZERO", "an adjustment needs a positive quantity"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<AdjustmentDraft>.Fail(new DomainError("REASON_REQUIRED", "an adjustment needs a reason"));
        }

        // BG-007 approval policy: adjustments move stock without a sale, so
        // they record who approved (the session service resolves approval for
        // the accepted role; a self-approving operator fills its own id).
        if (cmd.ApprovedBy is null)
        {
            return Result<AdjustmentDraft>.Fail(new DomainError(
                "ADJUSTMENT_APPROVAL_REQUIRED", "an adjustment needs an operator approval"));
        }

        Result<bool> dayGate = VoidBuilder.DayGate(ctx, cmd.Day);
        if (dayGate.IsFailure)
        {
            return Result<AdjustmentDraft>.Fail(dayGate.Errors);
        }

        bool decreases = cmd.Kind is AdjustmentKind.Decrease or AdjustmentKind.Damage or AdjustmentKind.Loss;
        if (decreases && ctx.State.StockOnHand(cmd.ProductId, cmd.Location).Units < cmd.Quantity.Units)
        {
            return Result<AdjustmentDraft>.Fail(new DomainError("STOCK_INSUFFICIENT", "not enough stock to decrease"));
        }

        Movement movement = cmd.Kind switch
        {
            AdjustmentKind.Increase => Moves.AdjustmentIncrease(
                0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason),
            AdjustmentKind.Decrease => Moves.AdjustmentDecrease(
                0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason),
            AdjustmentKind.Damage => Moves.Damage(
                0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason),
            AdjustmentKind.Loss => Moves.Loss(
                0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd.Kind)),
        };

        var document = new Adjustment(
            cmd.Id, cmd.Day, cmd.ProductId, cmd.Location, cmd.Kind, cmd.Reason, cmd.By, cmd.NowUtc, cmd.ApprovedBy);
        return Result<AdjustmentDraft>.Ok(new AdjustmentDraft(document, new[] { movement }));
    }
}

public static class CountBuilder
{
    public static Result<CountDraft> Build(LedgerContext ctx, CountCommand cmd)
    {
        var errors = new List<DomainError>();
        var movements = new List<Movement>(cmd.Lines.Count);
        var lines = new List<CountLine>(cmd.Lines.Count);

        foreach (CountLineCommand line in cmd.Lines)
        {
            lines.Add(new CountLine(line.ProductId, line.Observed, line.Notes));
            movements.Add(Moves.CountObserved(
                0, cmd.NowUtc, cmd.By, new CountRef(cmd.Id), line.ProductId, cmd.Location, line.Observed));
        }

        if (errors.Count > 0)
        {
            return Result<CountDraft>.Fail(errors);
        }

        var count = new PhysicalCount(cmd.Id, cmd.Location, lines, cmd.By, cmd.NowUtc);
        return Result<CountDraft>.Ok(new CountDraft(count, movements));
    }
}

/// <summary>
/// The day lifecycle builders (docs/decisions/0004 §2, §6): open records the
/// float, close records expected/counted/difference/label, reopen requires a
/// reason and an approver and never erases the prior close. The lifecycle
/// documents carry no movements — the day gate is document-driven; the fold
/// derives the lifecycle state in the P1b slice.
/// </summary>
public static class DayBuilder
{
    public static Result<DayOpenDraft> Open(LedgerContext ctx, DayOpenCommand cmd)
    {
        DayLifecycleState state = ctx.State.Day(cmd.Day);
        if (state != DayLifecycleState.New)
        {
            return Result<DayOpenDraft>.Fail(new DomainError(
                "DAY_NOT_NEW", $"day {cmd.Day} is {state}; only a new day can be opened"));
        }

        if (cmd.OpeningFloatCentavos < 0)
        {
            return Result<DayOpenDraft>.Fail(new DomainError("FLOAT_NEGATIVE", "the opening float cannot be negative"));
        }

        return Result<DayOpenDraft>.Ok(new DayOpenDraft(
            new DayOpen(cmd.Id, cmd.Day, cmd.OpeningFloatCentavos, cmd.By, cmd.NowUtc)));
    }

    public static Result<DayCloseDraft> Close(LedgerContext ctx, DayCloseCommand cmd)
    {
        if (ctx.State.Day(cmd.Day) != DayLifecycleState.Open)
        {
            return Result<DayCloseDraft>.Fail(new DomainError(
                "DAY_NOT_OPEN", $"day {cmd.Day} is {ctx.State.Day(cmd.Day)}; only an open day can be closed"));
        }

        if (cmd.CountedCashCentavos < 0)
        {
            return Result<DayCloseDraft>.Fail(new DomainError("COUNT_NEGATIVE", "the counted cash cannot be negative"));
        }

        // Expected cash per docs/decisions/0004 §1, derived by the day-keyed
        // fold: opening float + cash sales − cash refunds/revokes − approved
        // cash expenses (FundOut) + cash pay-downs received (FundIn). The
        // fold is the single source of truth; the close document records it.
        long expectedCash = ctx.State.ExpectedCash(cmd.Day);
        long difference = expectedCash - cmd.CountedCashCentavos;
        DayBalanceLabel label = difference == 0
            ? DayBalanceLabel.Balanced
            : difference > 0
                ? DayBalanceLabel.Shortage
                : DayBalanceLabel.Overage;

        return Result<DayCloseDraft>.Ok(new DayCloseDraft(
            new DayClose(cmd.Id, cmd.Day, expectedCash, cmd.CountedCashCentavos, difference, label, cmd.Notes, cmd.By, cmd.NowUtc)));
    }

    public static Result<DayReopenDraft> Reopen(LedgerContext ctx, DayReopenCommand cmd)
    {
        if (ctx.State.Day(cmd.Day) != DayLifecycleState.Closed)
        {
            return Result<DayReopenDraft>.Fail(new DomainError(
                "DAY_NOT_CLOSED", $"day {cmd.Day} is {ctx.State.Day(cmd.Day)}; only a closed day can reopen"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<DayReopenDraft>.Fail(new DomainError("REASON_REQUIRED", "a reopen needs an authorized reason"));
        }

        return Result<DayReopenDraft>.Ok(new DayReopenDraft(
            new DayReopen(cmd.Id, cmd.Day, cmd.Reason, cmd.ApprovedBy, cmd.By, cmd.NowUtc)));
    }
}
