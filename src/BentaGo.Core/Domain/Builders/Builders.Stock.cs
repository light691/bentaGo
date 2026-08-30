using BentaGo.Core.Common;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Trips;

namespace BentaGo.Core.Domain.Builders;

public sealed record LoadDraft(StockLoad Document, IReadOnlyList<Movement> Movements);

public sealed record TransferDraft(TransferId Id, IReadOnlyList<Movement> Movements);

public sealed record AdjustmentDraft(Adjustment Document, IReadOnlyList<Movement> Movements);

public sealed record CountDraft(PhysicalCount Document, IReadOnlyList<Movement> Movements);

public sealed record CloseDraft(DailyClose Document, IReadOnlyList<Movement> Movements);

public sealed record ReopenDraft(ReopenRef Document, IReadOnlyList<Movement> Movements);

public sealed record CancelDraft(CancelRef Document, IReadOnlyList<Movement> Movements);

public sealed record TripDraft(Trip Document, IReadOnlyList<Movement> Movements);

public static class TripBuilder
{
    public static Result<TripDraft> Build(LedgerContext ctx, NewTripCommand cmd)
    {
        if (ctx.FindTrip(cmd.Id) is not null)
        {
            return Result<TripDraft>.Fail(new DomainError("DUP_TRIP", $"trip {cmd.Id} already exists"));
        }

        // One active trip per peddler (docs/00:47, docs/01:75). A peddler
        // occupies the single slot from Draft until a terminal state — letting
        // a second Draft exist would let loads create two active trips
        // retroactively, so the check covers every non-terminal status. The
        // service runs this inside the store's registered-document set; the
        // SQLite store commits the trip in its own one-row transaction.
        foreach (Trip existingTrip in ctx.Trips)
        {
            if (existingTrip.Peddler != cmd.Peddler)
            {
                continue;
            }

            TripStatus existing = ctx.State.Trip(existingTrip.Id).Status;
            if (existing is not (TripStatus.Closed or TripStatus.Cancelled))
            {
                return Result<TripDraft>.Fail(new DomainError(
                    "TRIP_ALREADY_ACTIVE",
                    $"peddler {cmd.Peddler} already has an active trip {existingTrip.Id} ({existing}); one active trip per peddler"));
            }
        }

        var trip = new Trip(cmd.Id, cmd.Peddler, cmd.Location, cmd.NowUtc, cmd.By);
        return Result<TripDraft>.Ok(new TripDraft(trip, Array.Empty<Movement>()));
    }
}

public static class LoadBuilder
{
    public static Result<LoadDraft> Build(LedgerContext ctx, LoadCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.LoadNumber))
        {
            return Result<LoadDraft>.Fail(new DomainError("LOAD_NUMBER_REQUIRED", "a load needs a load number"));
        }

        foreach (StockLoad existing in ctx.Loads)
        {
            if (existing.LoadNumber == cmd.LoadNumber)
            {
                return Result<LoadDraft>.Fail(new DomainError("DUP_LOAD", $"load number {cmd.LoadNumber} already used"));
            }
        }

        Trip? trip = ctx.FindTrip(cmd.Trip);
        if (trip is null)
        {
            return Result<LoadDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        // Loads are legal in Draft/Loading/Active ("initial and additional
        // loads on the same day" — docs/01:88); a terminal trip never takes
        // stock. The fold re-checks every transfer; this is the explicit
        // phase gate so the caller sees the lifecycle vocabulary.
        TripStatus loadStatus = ctx.State.Trip(cmd.Trip).Status;
        if (loadStatus == TripStatus.Closed)
        {
            return Result<LoadDraft>.Fail(new DomainError("TRIP_CLOSED", $"trip {cmd.Trip} is closed and cannot be loaded"));
        }

        if (loadStatus == TripStatus.Cancelled)
        {
            return Result<LoadDraft>.Fail(new DomainError("TRIP_CANCELLED", $"trip {cmd.Trip} is cancelled and cannot be loaded"));
        }

        var errors = new List<DomainError>();
        var movements = new List<Movement>(cmd.Lines.Count * 2);
        var lines = new List<StockLoadLine>(cmd.Lines.Count);

        foreach (LoadLine line in cmd.Lines)
        {
            if (line.Quantity.IsZero)
            {
                errors.Add(new DomainError("LINE_ZERO", "a load line needs a positive quantity", line.Sku));
                continue;
            }

            lines.Add(new StockLoadLine(line.ProductId, line.Sku, line.ProductName, line.Quantity, line.UnitCost));
            movements.Add(Moves.TransferOut(
                0, cmd.NowUtc, cmd.By, new LoadRef(cmd.Id), line.ProductId, cmd.From, line.Quantity));
            movements.Add(Moves.TransferIn(
                0, cmd.NowUtc, cmd.By, new LoadRef(cmd.Id), line.ProductId, trip.Location, line.Quantity));
        }

        if (errors.Count > 0)
        {
            return Result<LoadDraft>.Fail(errors);
        }

        var load = new StockLoad(cmd.Id, cmd.LoadNumber, cmd.Trip, lines, cmd.By, cmd.NowUtc);
        return Result<LoadDraft>.Ok(new LoadDraft(load, movements));
    }
}

public static class TransferBuilder
{
    public static Result<TransferDraft> Build(LedgerContext ctx, TransferCommand cmd)
    {
        if (cmd.From == cmd.To)
        {
            return Result<TransferDraft>.Fail(new DomainError("SAME_LOCATION", "transfer source and destination are the same"));
        }

        if (cmd.Quantity.IsZero)
        {
            return Result<TransferDraft>.Fail(new DomainError("LINE_ZERO", "a transfer needs a positive quantity"));
        }

        if (ctx.State.StockOnHand(cmd.ProductId, cmd.From).Units < cmd.Quantity.Units)
        {
            return Result<TransferDraft>.Fail(new DomainError("STOCK_INSUFFICIENT", "not enough stock at the source"));
        }

        var movements = new List<Movement>
        {
            Moves.TransferOut(0, cmd.NowUtc, cmd.By, new TransferRef(cmd.Id), cmd.ProductId, cmd.From, cmd.Quantity),
            Moves.TransferIn(0, cmd.NowUtc, cmd.By, new TransferRef(cmd.Id), cmd.ProductId, cmd.To, cmd.Quantity),
        };

        return Result<TransferDraft>.Ok(new TransferDraft(cmd.Id, movements));
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

        Result<bool> tripGate = VoidBuilder.TripGate(ctx, cmd.Trip);
        if (tripGate.IsFailure)
        {
            return Result<AdjustmentDraft>.Fail(tripGate.Errors);
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
            cmd.Id, cmd.Trip, cmd.ProductId, cmd.Location, cmd.Kind, cmd.Reason, cmd.By, cmd.NowUtc, cmd.ApprovedBy);
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

public static class CloseBuilder
{
    public static Result<CloseDraft> Build(LedgerContext ctx, CloseCommand cmd)
    {
        Trip? trip = ctx.FindTrip(cmd.Trip);
        if (trip is null)
        {
            return Result<CloseDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        TripStatus status = ctx.State.Trip(cmd.Trip).Status;
        if (status == TripStatus.Closed)
        {
            return Result<CloseDraft>.Fail(new DomainError("TRIP_CLOSED", $"trip {cmd.Trip} is already closed"));
        }

        if (status == TripStatus.Cancelled)
        {
            return Result<CloseDraft>.Fail(new DomainError("TRIP_CANCELLED", $"trip {cmd.Trip} is cancelled and cannot be closed"));
        }

        if (status == TripStatus.Planned)
        {
            return Result<CloseDraft>.Fail(new DomainError("TRIP_NOT_OPEN", $"trip {cmd.Trip} is not open"));
        }

        // Expected cash is the physical cash this trip's own sales produced —
        // never the global till or another fund (a GCash sale is not "counted
        // cash"), or closing one peddler would read another's or a digital fund.
        Money expectedCash = ctx.State.TripCash(cmd.Trip, Fund.PhysicalCash);
        Money variance = expectedCash.Minus(cmd.CountedCash);

        var close = new DailyClose(cmd.Id, cmd.Trip, expectedCash, cmd.CountedCash, variance, cmd.Notes, cmd.By, cmd.NowUtc);
        var movements = new List<Movement>
        {
            Moves.TripClose(0, cmd.NowUtc, cmd.By, new CloseRef(cmd.Id), cmd.Trip),
        };

        return Result<CloseDraft>.Ok(new CloseDraft(close, movements));
    }
}

public static class ReopenBuilder
{
    public static Result<ReopenDraft> Build(LedgerContext ctx, ReopenCommand cmd)
    {
        if (ctx.FindTrip(cmd.Trip) is null)
        {
            return Result<ReopenDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        TripStatus status = ctx.State.Trip(cmd.Trip).Status;
        if (status != TripStatus.Closed)
        {
            return Result<ReopenDraft>.Fail(new DomainError("TRIP_NOT_CLOSED", $"trip {cmd.Trip} is {status}, only a closed trip can reopen"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<ReopenDraft>.Fail(new DomainError("REASON_REQUIRED", "a reopen needs an authorized reason"));
        }

        var movements = new List<Movement>
        {
            Moves.TripReopen(0, cmd.NowUtc, cmd.By, new ReopenRef(cmd.Trip), cmd.Trip, cmd.Reason),
        };

        return Result<ReopenDraft>.Ok(new ReopenDraft(new ReopenRef(cmd.Trip), movements));
    }
}

/// <summary>
/// Cancellation of a trip before any sale (docs/01:45), with an authorized
/// reason. The state machine (Draft/Loading only) is the phase gate; the
/// revert of each issued load line is a TransferOut(from the trip location)
/// + TransferIn(to the load's original source, derived from the movement
/// stream) pair carrying the SAME LoadRef — one atomic batch with the
/// TripCancel marker (BG-005c). "Cancelled trips don't create stock deltas"
/// (brief): the marker itself has no balance effect; the revert is what puts
/// stock back.
/// </summary>
public static class CancelBuilder
{
    public static Result<CancelDraft> Build(LedgerContext ctx, IReadOnlyList<Movement> movements, CancelTripCommand cmd)
    {
        Trip? trip = ctx.FindTrip(cmd.Trip);
        if (trip is null)
        {
            return Result<CancelDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<CancelDraft>.Fail(new DomainError("REASON_REQUIRED", "a cancel needs an authorized reason"));
        }

        bool hasSales = false;
        foreach (Sale sale in ctx.Sales)
        {
            if (sale.Trip == cmd.Trip)
            {
                hasSales = true;
                break;
            }
        }

        // The docs' phase gate through the pure machine: cancellation is
        // legal only from Draft/Loading (never after a sale — docs/01:45).
        TripPhase phase = TripLifecycle.PhaseOf(ctx.State.Trip(cmd.Trip), hasSales);
        Result<TripPhase> gate = TripLifecycle.Transition(phase, TripAction.Cancel);
        if (gate.IsFailure)
        {
            return Result<CancelDraft>.Fail(gate.Errors);
        }

        var reverted = new List<Movement>();
        foreach (StockLoad load in ctx.Loads)
        {
            if (load.Trip != cmd.Trip)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                LocationId? source = SourceOf(movements, load.Id, line.ProductId);
                if (source is null)
                {
                    return Result<CancelDraft>.Fail(new DomainError(
                        "TRIP_CANCEL_SOURCE_UNKNOWN",
                        $"cannot revert load {load.Id}: no TransferOut movement found for product {line.ProductId}"));
                }

                reverted.Add(Moves.TransferOut(
                    0, cmd.NowUtc, cmd.By, new LoadRef(load.Id), line.ProductId, trip.Location, line.Quantity));
                reverted.Add(Moves.TransferIn(
                    0, cmd.NowUtc, cmd.By, new LoadRef(load.Id), line.ProductId, source.Value, line.Quantity));
            }
        }

        reverted.Add(Moves.TripCancel(
            0, cmd.NowUtc, cmd.By, new CancelRef(cmd.Trip), cmd.Trip, cmd.Reason));
        return Result<CancelDraft>.Ok(new CancelDraft(new CancelRef(cmd.Trip), reverted));
    }

    /// <summary>The location a load line left from: the stream's TransferOut carrying that LoadRef.</summary>
    private static LocationId? SourceOf(IReadOnlyList<Movement> movements, LoadId loadId, ProductId product)
    {
        foreach (Movement movement in movements)
        {
            if (movement is TransferOut transferOut
                && transferOut.Document is LoadRef loadRef
                && loadRef.Id == loadId
                && transferOut.ProductId == product)
            {
                return transferOut.LocationId;
            }
        }

        return null;
    }
}
