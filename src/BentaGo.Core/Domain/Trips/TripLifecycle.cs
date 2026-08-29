using BentaGo.Core.Common;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Core.Domain.Trips;

/// <summary>
/// The trip lifecycle as specified by docs/01:42-54: a pure five-state state
/// machine (`Draft → Loading → Active → Reconciling → Closed`) plus the
/// documented cancellation state ("Cancellation is permitted only before
/// sales, with an authorized reason" — docs/01:45). This is BG-005c's
/// contract encoding: every legal and illegal transition is pinned here, in
/// Core, with the same DomainError codes the builders/services return, so the
/// machine is unit-testable with no storage and no services.
///
/// Interpretation notes (docs are silent/coarse on mechanics; conservative
/// choices recorded):
///   - `Draft` maps to the fold's <see cref="TripStatus.Planned"/>; `Active`
///     maps to <see cref="TripStatus.Open"/> once the trip has sales. The
///     fold's own operational status stays Planned/Open/Closed (existing
///     tests pin that; docs/02:62 closes in ONE transaction), while this
///     machine is the authoritative legality table for the service layer.
///   - `Reconciling` (peddler submits the close, docs/01:53) is a machine
///     phase: the current close use case performs submit+confirm in one
///     transaction (docs/02:60-62), so the fold never persists Reconciling —
///     the table still models it and its transitions for the (future) split
///     workflow (BG-005d).
///   - "One active trip per peddler" (docs/00:47) = a peddler may occupy the
///     slot from Draft until a terminal state (Closed/Cancelled) — enforced
///     by the service/builder, never silently allowed.
///   - Issuing stock is legal in Draft, Loading, AND Active ("Support initial
///     and additional loads on the same day" — docs/01:88); a close is legal
///     from Loading (return everything) and Active; reopen only from Closed
///     ("Admin reviews variances and closes or reopens with a reason" —
///     docs/01:54).
/// </summary>
public enum TripPhase
{
    /// <summary>docs: Draft — admin created the trip; no stock issued.</summary>
    Draft,

    /// <summary>docs: Loading — stock issued, no sales yet.</summary>
    Loading,

    /// <summary>docs: Active — the peddler started selling.</summary>
    Active,

    /// <summary>docs: Reconciling — close submitted, awaiting admin review.</summary>
    Reconciling,

    /// <summary>docs: Closed — admin closed the trip.</summary>
    Closed,

    /// <summary>docs: cancelled before sales, with an authorized reason.</summary>
    Cancelled,
}

/// <summary>The operations the trip lifecycle recognizes.</summary>
public enum TripAction
{
    /// <summary>Issue an initial or additional stock load (docs/01:88).</summary>
    IssueLoad,

    /// <summary>Start selling — the first sale on the trip (docs/01:49).</summary>
    StartSelling,

    /// <summary>Peddler submits the trip close (docs/01:53).</summary>
    SubmitClose,

    /// <summary>Admin closes (docs/01:54, docs/02:62 — one transaction).</summary>
    Close,

    /// <summary>Admin reopens a closed trip with a reason (docs/01:54).</summary>
    Reopen,

    /// <summary>Cancel the trip before any sale, with an authorized reason (docs/01:45).</summary>
    Cancel,
}

public static class TripLifecycle
{
    /// <summary>
    /// Executes one action from one phase: Ok(next phase) when legal, else a
    /// DomainError with the code the builders/services surface.
    /// </summary>
    public static Result<TripPhase> Transition(TripPhase from, TripAction action)
    {
        switch (action)
        {
            case TripAction.IssueLoad:
                return Canon(from, TripPhase.Draft, TripPhase.Loading, TripPhase.Active)
                    ? Ok(from == TripPhase.Active ? TripPhase.Active : TripPhase.Loading)
                    : Fail(from, action, "a load is only legal before the close (Draft/Loading/Active)");

            case TripAction.StartSelling:
                if (from == TripPhase.Loading)
                {
                    return Ok(TripPhase.Active);
                }

                return Fail(from, action, from == TripPhase.Draft
                    ? "a trip must receive stock before it can start selling"
                    : "selling can only start from Loading");

            case TripAction.SubmitClose:
                return Canon(from, TripPhase.Loading, TripPhase.Active)
                    ? Ok(TripPhase.Reconciling)
                    : Fail(from, action, "only an active (or loaded) trip can submit a close");

            case TripAction.Close:
                return Canon(from, TripPhase.Loading, TripPhase.Active, TripPhase.Reconciling)
                    ? Ok(TripPhase.Closed)
                    : Fail(from, action, "a close is only legal from Loading/Active/Reconciling");

            case TripAction.Reopen:
                return from == TripPhase.Closed
                    ? Ok(TripPhase.Active)
                    : Fail(from, action, "only a closed trip can reopen");

            case TripAction.Cancel:
                return Canon(from, TripPhase.Draft, TripPhase.Loading)
                    ? Ok(TripPhase.Cancelled)
                    : Fail(from, action, from is TripPhase.Active or TripPhase.Reconciling
                        ? "a trip with sales cannot be cancelled (docs: cancellation only before sales)"
                        : "only a Draft or Loading trip can be cancelled");

            default:
                return Result<TripPhase>.Fail(new DomainError("UNKNOWN_ACTION", $"unknown trip action {action}"));
        }
    }

    /// <summary>True when the phase is one the fold keeps (never persisted-in-progress phases).</summary>
    public static bool IsTerminal(TripPhase phase) => phase is TripPhase.Closed or TripPhase.Cancelled;

    /// <summary>
    /// Maps the fold's operational state plus the has-sales fact onto the
    /// docs' phases (interpretation table above). <see cref="Reconciling"/> is
    /// never produced by the current one-step close (docs/02:62).
    /// </summary>
    public static TripPhase PhaseOf(TripState state, bool hasSales)
    {
        switch (state.Status)
        {
            case TripStatus.Planned:
                return TripPhase.Draft;
            case TripStatus.Open:
                return hasSales ? TripPhase.Active : TripPhase.Loading;
            case TripStatus.Closed:
                return TripPhase.Closed;
            case TripStatus.Cancelled:
                return TripPhase.Cancelled;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state.Status, "unknown fold trip status");
        }
    }

    private static bool Canon(TripPhase from, params TripPhase[] allowed) =>
        Array.IndexOf(allowed, from) >= 0;

    private static Result<TripPhase> Ok(TripPhase next) => Result<TripPhase>.Ok(next);

    private static Result<TripPhase> Fail(TripPhase from, TripAction action, string message)
    {
        string code = CodeFor(from, action);
        return Result<TripPhase>.Fail(new DomainError(code, $"{action} from {from}: {message}"));
    }

    /// <summary>
    /// The DomainError codes the services/builders surface when an action is
    /// attempted from an illegal phase. Codes deliberately mirror the existing
    /// builder codes where they overlap (TRIP_NOT_OPEN, TRIP_CLOSED,
    /// TRIP_NOT_CLOSED) so callers see one vocabulary.
    /// </summary>
    private static string CodeFor(TripPhase from, TripAction action) => (from, action) switch
    {
        (TripPhase.Draft, TripAction.StartSelling) => "TRIP_NOT_OPEN",
        (TripPhase.Draft, TripAction.SubmitClose) => "TRIP_NOT_OPEN",
        (TripPhase.Draft, TripAction.Close) => "TRIP_NOT_OPEN",
        (TripPhase.Closed, TripAction.IssueLoad) => "TRIP_CLOSED",
        (TripPhase.Closed, TripAction.StartSelling) => "TRIP_CLOSED",
        (TripPhase.Closed, TripAction.SubmitClose) => "TRIP_CLOSED",
        (TripPhase.Closed, TripAction.Close) => "TRIP_CLOSED",
        (TripPhase.Closed, TripAction.Cancel) => "TRIP_CLOSED",
        (TripPhase.Cancelled, TripAction.IssueLoad) => "TRIP_CANCELLED",
        (TripPhase.Cancelled, TripAction.StartSelling) => "TRIP_CANCELLED",
        (TripPhase.Cancelled, TripAction.SubmitClose) => "TRIP_CANCELLED",
        (TripPhase.Cancelled, TripAction.Close) => "TRIP_CANCELLED",
        (TripPhase.Cancelled, TripAction.Reopen) => "TRIP_CANCELLED",
        (TripPhase.Cancelled, TripAction.Cancel) => "TRIP_CANCELLED",
        (TripPhase.Reconciling, TripAction.IssueLoad) => "TRIP_RECONCILING",
        (TripPhase.Reconciling, TripAction.StartSelling) => "TRIP_RECONCILING",
        (TripPhase.Reconciling, TripAction.SubmitClose) => "TRIP_RECONCILING",
        (TripPhase.Reconciling, TripAction.Cancel) => "TRIP_HAS_SALES",
        (TripPhase.Active, TripAction.Cancel) => "TRIP_HAS_SALES",
        (TripPhase.Active, TripAction.StartSelling) => "TRIP_ALREADY_ACTIVE",
        (TripPhase.Loading, TripAction.Reopen) => "TRIP_NOT_CLOSED",
        (TripPhase.Active, TripAction.Reopen) => "TRIP_NOT_CLOSED",
        (TripPhase.Draft, TripAction.Reopen) => "TRIP_NOT_CLOSED",
        (TripPhase.Reconciling, TripAction.Reopen) => "TRIP_NOT_CLOSED",
        (TripPhase.Draft, TripAction.IssueLoad) => "TRIP_TRANSITION_ILLEGAL",
        _ => "TRIP_TRANSITION_ILLEGAL",
    };
}