using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Trips;

namespace BentaGo.Application.Services;

/// <summary>
/// The nine store-agnostic use cases (BG-003 §9.3): each is
/// builder -> Register* (stage) -> Bind -> Append(AppendBatch) with one
/// attached audit event. The store's Append transaction IS the use case's
/// transaction, so staging then failing to build commits nothing.
///
/// F12 terminal/reason plumbing (BG-005d): every use-case audit sets Terminal
/// to Environment.MachineName — the same source the login rows use
/// (docs/01:77) — because the terminal is knowable at call time. Reason is
/// set where the command carries one (void, adjustment, reopen, cancel).
/// Audits that are NOT use-case actions keep both null — the scenario
/// diagnostics (LANE9 rows recording rejected operations) and batch-style
/// rows — because no single terminal performed them.
/// </summary>
public sealed class OpenTripService(ILedgerStore store) : IOpenTripService
{
    public Result<Trip> OpenTrip(NewTripCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<TripDraft> draft = TripBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<Trip>.Fail(draft.Errors);
        }

        store.RegisterTrip(draft.Value.Document);

        // Every trip transition is audited (brief line 6). docs/02:100 names
        // "closes" among audited actions; trip lifecycle events use the
        // canonical TRIP.* set (BG-005d): TRIP.OPEN here.
        store.RecordAudit(new AuditEvent(
            -1, cmd.By, cmd.NowUtc, TripAudit.TripOpenCategory,
            $"trip {cmd.Id.Value:N} opened for peddler {cmd.Peddler.Value:N}", new ReopenRef(cmd.Id),
            Terminal: Environment.MachineName));
        return Result<Trip>.Ok(draft.Value.Document);
    }
}

public sealed class LoadService(ILedgerStore store) : ILoadService
{
    public Result<LoadOutcome> Load(LoadCommand cmd, PeddlerId peddler)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Trip? trip = store.Context.FindTrip(cmd.Trip);
        if (trip is not null && trip.Peddler != peddler)
        {
            // Cross-peddler access to a load is rejected, never silent
            // (brief: trips/loads take the active peddler).
            return Result<LoadOutcome>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH",
                $"trip {cmd.Trip} belongs to peddler {trip.Peddler}, not {peddler}"));
        }

        Result<LoadDraft> draft = LoadBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<LoadOutcome>.Fail(draft.Errors);
        }

        store.RegisterLoad(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                // LOAD stays the load category (it IS the load event): the
                // canonical trip set only covers lifecycle transitions.
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "LOAD",
                    $"load {cmd.LoadNumber} committed", new LoadRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<LoadOutcome>.Fail(outcome.Errors);
        }

        return Result<LoadOutcome>.Ok(new LoadOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class CheckoutService(ILedgerStore store) : ICheckoutService
{
    public Result<CheckoutOutcome> Checkout(CheckoutKey key, NewSaleCommand cmd)
    {
        string keyText = key.Value.ToString("D");

        // Pre-check fast path (D1): an already-committed key returns the SAME
        // receipt reference without touching the store's transaction.
        Result<CheckoutReference?> found = store.FindCheckout(keyText);
        if (found.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(found.Errors);
        }

        if (found.Value is { } existing)
        {
            return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(existing, Replayed: true, Receipt: null));
        }

        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(draft.Errors);
        }

        store.RegisterSale(draft.Value.Document);
        store.Bind();

        var reference = new CheckoutReference(draft.Value.Document.ReceiptNumber, draft.Value.Document.Id);
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "SALE",
                    $"receipt {reference.ReceiptNumber} committed", new SaleRef(draft.Value.Document.Id),
                    Terminal: Environment.MachineName),
            },
            Idempotency: new IdempotencyMapping(keyText, reference.ToPayloadString())));

        if (outcome.IsFailure)
        {
            return Result<CheckoutOutcome>.Fail(outcome.Errors);
        }

        if (outcome.Value.Replayed)
        {
            // Authoritative replay: the append detected the key collision
            // inside its transaction and rolled back (D1).
            try
            {
                CheckoutReference replayed = CheckoutReference.FromPayload(outcome.Value.ReplayedPayload!);
                return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(replayed, Replayed: true, Receipt: null));
            }
            catch (InvalidOperationException)
            {
                return Result<CheckoutOutcome>.Fail(new DomainError(
                    "LEDGER_CORRUPT", "stored idempotency payload is not a checkout reference"));
            }
        }

        return Result<CheckoutOutcome>.Ok(new CheckoutOutcome(
            reference, Replayed: false, Receipt: outcome.Value.Receipt));
    }
}

public sealed class VoidService(ILedgerStore store) : IVoidService
{
    public Result<VoidOutcome> Void(VoidCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<VoidDraft> draft = VoidBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<VoidOutcome>.Fail(draft.Errors);
        }

        store.RegisterVoid(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "VOID",
                    $"sale {cmd.Sale} voided: {cmd.Reason}", new VoidRef(cmd.Id),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<VoidOutcome>.Fail(outcome.Errors);
        }

        return Result<VoidOutcome>.Ok(new VoidOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class ReturnService(ILedgerStore store) : IReturnService
{
    public Result<ReturnOutcome> Return(ReturnCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<ReturnDraft> draft = ReturnBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<ReturnOutcome>.Fail(draft.Errors);
        }

        store.RegisterReturn(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                // No top-level reason on ReturnCommand (reasons live per line);
                // the reason column stays null on purpose.
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "RETURN",
                    $"return {cmd.Id} on sale {cmd.OriginalSale} committed", new ReturnRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<ReturnOutcome>.Fail(outcome.Errors);
        }

        return Result<ReturnOutcome>.Ok(new ReturnOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class AdjustmentService(ILedgerStore store) : IAdjustmentService
{
    public Result<AdjustmentOutcome> Adjust(AdjustmentCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<AdjustmentDraft> draft = AdjustmentBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<AdjustmentOutcome>.Fail(draft.Errors);
        }

        store.RegisterAdjustment(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "ADJUSTMENT",
                    $"adjustment {cmd.Id} committed", new AdjustmentRef(cmd.Id),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<AdjustmentOutcome>.Fail(outcome.Errors);
        }

        return Result<AdjustmentOutcome>.Ok(new AdjustmentOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class CountService(ILedgerStore store) : ICountService
{
    public Result<CountOutcome> Count(CountCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<CountDraft> draft = CountBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CountOutcome>.Fail(draft.Errors);
        }

        store.RegisterCount(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, "COUNT",
                    $"count {cmd.Id} committed", new CountRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<CountOutcome>.Fail(outcome.Errors);
        }

        return Result<CountOutcome>.Ok(new CountOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class CloseService(ILedgerStore store) : ICloseService
{
    public Result<CloseOutcome> Close(CloseCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<CloseDraft> draft = CloseBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<CloseOutcome>.Fail(draft.Errors);
        }

        store.RegisterClose(draft.Value.Document);
        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, TripAudit.TripCloseCategory,
                    $"trip {cmd.Trip} closed (counted {draft.Value.Document.CountedCash.ToPesos():0.00})", new CloseRef(cmd.Id),
                    Terminal: Environment.MachineName),
            }));
        if (outcome.IsFailure)
        {
            return Result<CloseOutcome>.Fail(outcome.Errors);
        }

        return Result<CloseOutcome>.Ok(new CloseOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

public sealed class ReopenService(ILedgerStore store) : IReopenService
{
    public Result<AppendReceipt> Reopen(ReopenCommand cmd)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Result<ReopenDraft> draft = ReopenBuilder.Build(store.Context, cmd);
        if (draft.IsFailure)
        {
            return Result<AppendReceipt>.Fail(draft.Errors);
        }

        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, TripAudit.TripReopenCategory,
                    $"trip {cmd.Trip} reopened: {cmd.Reason}", new ReopenRef(cmd.Trip),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<AppendReceipt>.Fail(outcome.Errors);
        }

        return Result<AppendReceipt>.Ok(outcome.Value.Receipt!);
    }
}

/// <summary>
/// Trip cancellation (BG-005c): the trip belongs to the acting peddler, the
/// pure state machine allows a pre-sales cancel, and any issued loads revert
/// in the SAME append transaction as the TripCancel marker (one unit of
/// work — brief). The transition audits under the canonical trip category
/// TRIP.CANCEL (BG-005d), with the command's reason in the Reason column.
/// </summary>
public sealed class CancelTripService(ILedgerStore store) : ICancelTripService
{
    public Result<CancelOutcome> Cancel(CancelTripCommand cmd, PeddlerId peddler)
    {
        store.Bind(); // refresh committed document sets (cross-process trips included)
        Trip? trip = store.Context.FindTrip(cmd.Trip);
        if (trip is null)
        {
            return Result<CancelOutcome>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {cmd.Trip}"));
        }

        if (trip.Peddler != peddler)
        {
            return Result<CancelOutcome>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH",
                $"trip {cmd.Trip} belongs to peddler {trip.Peddler}, not {peddler}"));
        }

        Result<CancelDraft> draft = CancelBuilder.Build(store.Context, store.Movements, cmd);
        if (draft.IsFailure)
        {
            return Result<CancelOutcome>.Fail(draft.Errors);
        }

        store.Bind();
        Result<AppendOutcome> outcome = store.Append(new AppendBatch(
            draft.Value.Movements,
            Audits: new[]
            {
                new AuditEvent(-1, cmd.By, cmd.NowUtc, TripAudit.TripCancelCategory,
                    $"trip {cmd.Trip.Value:N} cancelled: {cmd.Reason} (loads reverted)", new CancelRef(cmd.Trip),
                    Terminal: Environment.MachineName, Reason: cmd.Reason),
            }));
        if (outcome.IsFailure)
        {
            return Result<CancelOutcome>.Fail(outcome.Errors);
        }

        return Result<CancelOutcome>.Ok(new CancelOutcome(draft.Value.Document, outcome.Value.Receipt!));
    }
}

/// <summary>
/// Peddler-scoped trip reads (BG-005c). Every method takes the peddler
/// explicitly (the IActorContext carries only the user id today — BG-005a —
/// so the peddler is passed through service methods per the brief; BG-005d
/// binds it to the signed-in user's PeddlerId). On-hand values come from the
/// movement fold, never a stored stock_qty.
/// </summary>
public sealed class TripReadService(ILedgerStore store) : ITripReadService
{
    public Result<IReadOnlyList<Trip>> ListTrips(PeddlerId peddler)
    {
        store.Bind();
        return Result<IReadOnlyList<Trip>>.Ok(store.Context.Trips
            .Where(t => t.Peddler == peddler)
            .OrderBy(t => t.CreatedUtc)
            .ThenBy(t => t.Id.Value)
            .ToList());
    }

    public Result<Trip> GetTrip(PeddlerId peddler, TripId tripId)
    {
        store.Bind();
        Trip? trip = store.Context.FindTrip(tripId);
        if (trip is null)
        {
            return Result<Trip>.Fail(new DomainError("TRIP_UNKNOWN", $"no trip {tripId}"));
        }

        if (trip.Peddler != peddler)
        {
            return Result<Trip>.Fail(new DomainError(
                "TRIP_PEDDLER_MISMATCH",
                $"trip {tripId} belongs to peddler {trip.Peddler}, not {peddler}"));
        }

        return Result<Trip>.Ok(trip);
    }

    public Result<Quantity> OnHand(PeddlerId peddler, ProductId product)
    {
        store.Bind();
        int total = 0;
        foreach (LocationId location in LocationsOf(peddler))
        {
            total += store.State.StockOnHand(product, location).Units;
        }

        return Result<Quantity>.Ok(Quantity.Of(total));
    }

    public Result<IReadOnlyList<OnHandRow>> OnHandAll(PeddlerId peddler)
    {
        store.Bind();
        var products = new HashSet<ProductId>();
        var skus = new Dictionary<ProductId, string?>();
        foreach (StockLoad load in store.Context.Loads)
        {
            if (load.Trip == default || !TripsOf(peddler).Contains(load.Trip))
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                products.Add(line.ProductId);
                skus.TryAdd(line.ProductId, line.Sku);
            }
        }

        foreach (Sale sale in store.Context.Sales)
        {
            if (sale.Peddler != peddler)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                products.Add(line.ProductId);
                skus.TryAdd(line.ProductId, line.Sku);
            }
        }

        var rows = new List<OnHandRow>(products.Count);
        foreach (ProductId product in products.OrderBy(p => p.Value))
        {
            int total = 0;
            foreach (LocationId location in LocationsOf(peddler))
            {
                total += store.State.StockOnHand(product, location).Units;
            }

            rows.Add(new OnHandRow(product, skus[product], Quantity.Of(total)));
        }

        return Result<IReadOnlyList<OnHandRow>>.Ok(rows);
    }

    private HashSet<TripId> TripsOf(PeddlerId peddler)
    {
        var trips = new HashSet<TripId>();
        foreach (Trip trip in store.Context.Trips)
        {
            if (trip.Peddler == peddler)
            {
                trips.Add(trip.Id);
            }
        }

        return trips;
    }

    private HashSet<LocationId> LocationsOf(PeddlerId peddler)
    {
        var locations = new HashSet<LocationId>();
        foreach (Trip trip in store.Context.Trips)
        {
            if (trip.Peddler == peddler)
            {
                locations.Add(trip.Location);
            }
        }

        return locations;
    }
}
