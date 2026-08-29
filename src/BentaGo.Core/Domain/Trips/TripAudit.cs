namespace BentaGo.Core.Domain.Trips;

/// <summary>
/// Audit categories for trip lifecycle events (BG-005d). Trip transitions are
/// audited per docs/02:100 ("closes" among the audited actions) and previously
/// reused the generic REOPEN/CLOSE categories (BG-005c interpretation #8 —
/// the muddle this set replaces). Each lifecycle transition now has its own
/// canonical category:
///   TRIP.OPEN    — OpenTripService (trip becomes usable)
///   TRIP.CLOSE   — CloseService (close with counted cash)
///   TRIP.REOPEN  — ReopenService (authorized reopen of a closed trip)
///   TRIP.CANCEL  — CancelTripService (pre-sales cancel, loads reverted)
/// LOAD stays the load category (it IS the load event, not a trip transition);
/// the generic CLOSE/REOPEN categories remain for non-trip event uses.
/// </summary>
public static class TripAudit
{
    public const string TripOpenCategory = "TRIP.OPEN";
    public const string TripCancelCategory = "TRIP.CANCEL";
    public const string TripCloseCategory = "TRIP.CLOSE";
    public const string TripReopenCategory = "TRIP.REOPEN";
}
