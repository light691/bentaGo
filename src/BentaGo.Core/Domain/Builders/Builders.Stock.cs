using BentaGo.Core.Common;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Core.Domain.Builders;

public sealed record LoadDraft(StockLoad Document, IReadOnlyList<Movement> Movements);

public sealed record TransferDraft(TransferId Id, IReadOnlyList<Movement> Movements);

public sealed record AdjustmentDraft(Adjustment Document, IReadOnlyList<Movement> Movements);

public sealed record CountDraft(PhysicalCount Document, IReadOnlyList<Movement> Movements);

public sealed record CloseDraft(DailyClose Document, IReadOnlyList<Movement> Movements);

public sealed record ReopenDraft(ReopenRef Document, IReadOnlyList<Movement> Movements);

public sealed record TripDraft(Trip Document, IReadOnlyList<Movement> Movements);

public static class TripBuilder
{
    public static Result<TripDraft> Build(LedgerContext ctx, NewTripCommand cmd)
    {
        if (ctx.FindTrip(cmd.Id) is not null)
        {
            return Result<TripDraft>.Fail(new DomainError("TRIP_UNKNOWN", $"trip {cmd.Id} already exists"));
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

        if (!cmd.Increase && ctx.State.StockOnHand(cmd.ProductId, cmd.Location).Units < cmd.Quantity.Units)
        {
            return Result<AdjustmentDraft>.Fail(new DomainError("STOCK_INSUFFICIENT", "not enough stock to decrease"));
        }

        var document = new Adjustment(cmd.Id, cmd.ProductId, cmd.Location, cmd.Reason, cmd.By, cmd.NowUtc);
        var movements = new List<Movement>
        {
            cmd.Increase
                ? Moves.AdjustmentIncrease(0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason)
                : Moves.AdjustmentDecrease(0, cmd.NowUtc, cmd.By, new AdjustmentRef(cmd.Id), cmd.ProductId, cmd.Location, cmd.Quantity, cmd.Reason),
        };

        return Result<AdjustmentDraft>.Ok(new AdjustmentDraft(document, movements));
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

        if (status == TripStatus.Planned)
        {
            return Result<CloseDraft>.Fail(new DomainError("TRIP_NOT_OPEN", $"trip {cmd.Trip} is not open"));
        }

        Money expectedCash = ctx.State.FundsOnHand(Fund.PhysicalCash);
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
