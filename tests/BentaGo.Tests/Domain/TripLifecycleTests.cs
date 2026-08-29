using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Trips;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// BG-005c domain tests: the pure trip state machine (every legal and every
/// illegal transition), cancellation semantics (revert loads atomically,
/// before-sales-only), the one-active-trip invariant, peddler isolation, and
/// the movements-derived on-hand read model. Pure/in-memory lanes — the
/// persisted lanes live in the Persistence bucket.
/// </summary>
public sealed class TripLifecycleTests
{
    // ------------------------------------------------------------------
    // The pure state machine: the full 6-phase × 6-action matrix.
    // ------------------------------------------------------------------

    private static readonly (TripPhase From, TripAction Action, TripPhase? To, string? Code)[] Expectations =
    {
        // Draft (docs: admin created the trip).
        (TripPhase.Draft, TripAction.IssueLoad, TripPhase.Loading, null),
        (TripPhase.Draft, TripAction.StartSelling, null, "TRIP_NOT_OPEN"),
        (TripPhase.Draft, TripAction.SubmitClose, null, "TRIP_NOT_OPEN"),
        (TripPhase.Draft, TripAction.Close, null, "TRIP_NOT_OPEN"),
        (TripPhase.Draft, TripAction.Reopen, null, "TRIP_NOT_CLOSED"),
        (TripPhase.Draft, TripAction.Cancel, TripPhase.Cancelled, null),
        // Loading (docs: stock issued, no sales yet).
        (TripPhase.Loading, TripAction.IssueLoad, TripPhase.Loading, null),
        (TripPhase.Loading, TripAction.StartSelling, TripPhase.Active, null),
        (TripPhase.Loading, TripAction.SubmitClose, TripPhase.Reconciling, null),
        (TripPhase.Loading, TripAction.Close, TripPhase.Closed, null),
        (TripPhase.Loading, TripAction.Reopen, null, "TRIP_NOT_CLOSED"),
        (TripPhase.Loading, TripAction.Cancel, TripPhase.Cancelled, null),
        // Active (docs: peddler is selling).
        (TripPhase.Active, TripAction.IssueLoad, TripPhase.Active, null),
        (TripPhase.Active, TripAction.StartSelling, null, "TRIP_ALREADY_ACTIVE"),
        (TripPhase.Active, TripAction.SubmitClose, TripPhase.Reconciling, null),
        (TripPhase.Active, TripAction.Close, TripPhase.Closed, null),
        (TripPhase.Active, TripAction.Reopen, null, "TRIP_NOT_CLOSED"),
        (TripPhase.Active, TripAction.Cancel, null, "TRIP_HAS_SALES"),
        // Reconciling (docs: close submitted).
        (TripPhase.Reconciling, TripAction.IssueLoad, null, "TRIP_RECONCILING"),
        (TripPhase.Reconciling, TripAction.StartSelling, null, "TRIP_RECONCILING"),
        (TripPhase.Reconciling, TripAction.SubmitClose, null, "TRIP_RECONCILING"),
        (TripPhase.Reconciling, TripAction.Close, TripPhase.Closed, null),
        (TripPhase.Reconciling, TripAction.Reopen, null, "TRIP_NOT_CLOSED"),
        (TripPhase.Reconciling, TripAction.Cancel, null, "TRIP_HAS_SALES"),
        // Closed.
        (TripPhase.Closed, TripAction.IssueLoad, null, "TRIP_CLOSED"),
        (TripPhase.Closed, TripAction.StartSelling, null, "TRIP_CLOSED"),
        (TripPhase.Closed, TripAction.SubmitClose, null, "TRIP_CLOSED"),
        (TripPhase.Closed, TripAction.Close, null, "TRIP_CLOSED"),
        (TripPhase.Closed, TripAction.Reopen, TripPhase.Active, null),
        (TripPhase.Closed, TripAction.Cancel, null, "TRIP_CLOSED"),
        // Cancelled — a terminal phase: everything is rejected.
        (TripPhase.Cancelled, TripAction.IssueLoad, null, "TRIP_CANCELLED"),
        (TripPhase.Cancelled, TripAction.StartSelling, null, "TRIP_CANCELLED"),
        (TripPhase.Cancelled, TripAction.SubmitClose, null, "TRIP_CANCELLED"),
        (TripPhase.Cancelled, TripAction.Close, null, "TRIP_CANCELLED"),
        (TripPhase.Cancelled, TripAction.Reopen, null, "TRIP_CANCELLED"),
        (TripPhase.Cancelled, TripAction.Cancel, null, "TRIP_CANCELLED"),
    };

    [Fact]
    public void StateMachine_EveryLegalAndIllegalTransition_Pinned()
    {
        foreach ((TripPhase from, TripAction action, TripPhase? to, string? code) in Expectations)
        {
            Result<TripPhase> result = TripLifecycle.Transition(from, action);
            if (to is { } expected)
            {
                Assert.True(result.IsSuccess, $"{action} from {from} should succeed: {result.Errors.FirstOrDefault()?.Code}");
                Assert.Equal(expected, result.Value);
            }
            else
            {
                Assert.True(result.IsFailure, $"{action} from {from} should fail");
                Assert.Equal(code, result.Errors[0].Code);
            }
        }
    }

    [Fact]
    public void StateMachine_PhaseOf_MapsFoldStatusesToDocsPhases()
    {
        Assert.Equal(TripPhase.Draft, TripLifecycle.PhaseOf(new TripState(TripStatus.Planned, null, null), hasSales: false));
        Assert.Equal(TripPhase.Loading, TripLifecycle.PhaseOf(new TripState(TripStatus.Open, null, null), hasSales: false));
        Assert.Equal(TripPhase.Active, TripLifecycle.PhaseOf(new TripState(TripStatus.Open, null, null), hasSales: true));
        Assert.Equal(TripPhase.Closed, TripLifecycle.PhaseOf(new TripState(TripStatus.Closed, null, null), hasSales: true));
        Assert.Equal(TripPhase.Cancelled, TripLifecycle.PhaseOf(new TripState(TripStatus.Cancelled, null, null), hasSales: false));
    }

    // ------------------------------------------------------------------
    // Cancellation semantics (service + fold, in-memory store).
    // ------------------------------------------------------------------

    private sealed record Day(
        InMemoryLedgerStore Store,
        UserId Admin,
        PeddlerId Peddler,
        PeddlerId Other,
        LocationId StoreLoc,
        LocationId Route,
        ProductId Product,
        TripId Trip,
        DateTimeOffset Now);

    private static Day NewDay()
    {
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        var store = new InMemoryLedgerStore();
        var day = new Day(
            store,
            new UserId(ids.Next("admin")),
            new PeddlerId(ids.Next("peddler")),
            new PeddlerId(ids.Next("other")),
            new LocationId(ids.Next("store")),
            new LocationId(ids.Next("route")),
            new ProductId(ids.Next("product")),
            new TripId(ids.Next("trip")),
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));

        Result<Trip> opened = new OpenTripService(store).OpenTrip(new NewTripCommand(day.Trip, day.Peddler, day.Route, day.Admin, day.Now));
        Assert.True(opened.IsSuccess, opened.Errors.FirstOrDefault()?.Code);

        // Opening stock at the main store.
        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "OPEN-1", default, Array.Empty<StockLoadLine>(), day.Admin, day.Now));
        store.Bind();
        Result<AppendReceipt> opening = store.Append(new[]
        {
            Moves.OpeningStock(0, day.Now, day.Admin, new LoadRef(openId), day.Product, day.StoreLoc, Quantity.Of(100)),
        });
        Assert.True(opening.IsSuccess, opening.Errors.FirstOrDefault()?.Code);
        return day;
    }

    private static void LoadOut(Day day, int qty, string number = "LD-1")
    {
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        Result<LoadOutcome> loaded = new LoadService(day.Store).Load(new LoadCommand(
            new LoadId(ids.Next($"load-{number}")), number, day.Trip, day.StoreLoc,
            new[] { new LoadLine(day.Product, "SKU", "Product", Quantity.Of(qty), Money.FromPesos(30m)) },
            day.Admin, day.Now), day.Peddler);
        Assert.True(loaded.IsSuccess, loaded.Errors.FirstOrDefault()?.Code);
    }

    [Fact]
    public void CancelAfterLoad_RevertsLoadsAtomically_OneBatch()
    {
        Day day = NewDay();
        LoadOut(day, 30);

        Assert.Equal(70, day.Store.State.StockOnHand(day.Product, day.StoreLoc).Units);
        Assert.Equal(30, day.Store.State.StockOnHand(day.Product, day.Route).Units);

        long before = day.Store.Movements.Count;
        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "route cancelled by admin", day.Admin, day.Now.AddHours(2)), day.Peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        // The revert + marker are one unit of work (3 movements in the batch).
        Assert.Equal(before + 3, day.Store.Movements.Count);
        Assert.Equal(MovementKind.TripCancel, day.Store.Movements[^1].Kind);
        Assert.Equal(0, day.Store.State.StockOnHand(day.Product, day.Route).Units);
        Assert.Equal(100, day.Store.State.StockOnHand(day.Product, day.StoreLoc).Units);
        Assert.Equal(TripStatus.Cancelled, day.Store.State.Trip(day.Trip).Status);
    }

    [Fact]
    public void CancelDraftTrip_NoLoads_NoRevertMovements()
    {
        Day day = NewDay();
        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "route cancelled", day.Admin, day.Now.AddHours(2)), day.Peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        // The batch is exactly the TripCancel marker: one movement, no transfers.
        Assert.Equal(cancelled.Value.Receipt.FirstSequence, cancelled.Value.Receipt.LastSequence);
        Assert.Single(day.Store.Movements, m => m.Kind == MovementKind.TripCancel);
        Assert.DoesNotContain(day.Store.Movements, m => m.Kind is MovementKind.TransferOut or MovementKind.TransferIn);
        Assert.Equal(TripStatus.Cancelled, day.Store.State.Trip(day.Trip).Status);
    }

    [Fact]
    public void CancelAfterSales_Rejected_TRIP_HAS_SALES()
    {
        Day day = NewDay();
        LoadOut(day, 10);
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        var sale = SaleBuilder.Build(day.Store.Context, new NewSaleCommand(
            new SaleId(ids.Next("sale")), day.Trip, "R-CANCEL-SALE", day.Admin, day.Now.AddHours(1),
            new[] { new NewSaleLine(new SaleLineId(ids.Next("line")), day.Product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(2), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("pay")), PaymentMethod.Cash, "", Money.FromPesos(90m), Money.FromPesos(90m), Money.Zero) }));
        Assert.True(sale.IsSuccess, sale.Errors.FirstOrDefault()?.Code);
        day.Store.RegisterSale(sale.Value.Document);
        day.Store.Bind();
        Result<AppendReceipt> appended = day.Store.Append(sale.Value.Movements);
        Assert.True(appended.IsSuccess, appended.Errors.FirstOrDefault()?.Code);

        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "too late", day.Admin, day.Now.AddHours(3)), day.Peddler);
        Assert.True(cancelled.IsFailure);
        Assert.Equal("TRIP_HAS_SALES", cancelled.Errors[0].Code);
        Assert.Equal(TripStatus.Open, day.Store.State.Trip(day.Trip).Status); // nothing changed
    }

    [Fact]
    public void Cancel_ClosedTrip_Rejected_TRIP_CLOSED_And_DoubleCancel_Rejected_TRIP_CANCELLED()
    {
        Day day = NewDay();
        LoadOut(day, 5);

        var close = CloseBuilder.Build(day.Store.Context, new CloseCommand(
            new CloseId(Guid.NewGuid()), day.Trip, Money.FromPesos(0m), null, day.Admin, day.Now.AddHours(2)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code);
        Result<AppendReceipt> closed = day.Store.Append(close.Value.Movements);
        Assert.True(closed.IsSuccess, closed.Errors.FirstOrDefault()?.Code);

        var cancel = new CancelTripService(day.Store);
        Result<CancelOutcome> onClosed = cancel.Cancel(
            new CancelTripCommand(day.Trip, "late cancel", day.Admin, day.Now.AddHours(3)), day.Peddler);
        Assert.True(onClosed.IsFailure);
        Assert.Equal("TRIP_CLOSED", onClosed.Errors[0].Code);

        // Reopen, cancel, then cancel again -> TRIP_CANCELLED.
        var reopen = ReopenBuilder.Build(day.Store.Context, new ReopenCommand(day.Trip, "admin review", day.Admin, day.Now.AddHours(3)));
        Assert.True(reopen.IsSuccess, reopen.Errors.FirstOrDefault()?.Code);
        day.Store.Append(reopen.Value.Movements);

        Result<CancelOutcome> first = cancel.Cancel(
            new CancelTripCommand(day.Trip, "now cancelling", day.Admin, day.Now.AddHours(4)), day.Peddler);
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);
        Result<CancelOutcome> second = cancel.Cancel(
            new CancelTripCommand(day.Trip, "again", day.Admin, day.Now.AddHours(4)), day.Peddler);
        Assert.True(second.IsFailure);
        Assert.Equal("TRIP_CANCELLED", second.Errors[0].Code);
    }

    [Fact]
    public void Cancel_RequiresAuthorizedReason()
    {
        Day day = NewDay();
        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, " ", day.Admin, day.Now.AddHours(2)), day.Peddler);
        Assert.True(cancelled.IsFailure);
        Assert.Equal("REASON_REQUIRED", cancelled.Errors[0].Code);
    }

    // ------------------------------------------------------------------
    // One active trip per peddler (service-level invariant, docs/00:47).
    // ------------------------------------------------------------------

    [Fact]
    public void OpenTrip_SecondActive_Rejected_TRIP_ALREADY_ACTIVE()
    {
        Day day = NewDay();
        var second = new TripId(new ScenarioIdSource("trip-lifecycle-test-v1").Next("trip-2"));
        Result<Trip> duplicate = new OpenTripService(day.Store).OpenTrip(
            new NewTripCommand(second, day.Peddler, day.Route, day.Admin, day.Now.AddHours(1)));
        Assert.True(duplicate.IsFailure);
        Assert.Equal("TRIP_ALREADY_ACTIVE", duplicate.Errors[0].Code);
    }

    [Fact]
    public void OpenTrip_AfterTerminalState_Allowed()
    {
        Day day = NewDay();
        LoadOut(day, 5);

        // Cancel the first trip -> terminal; a new trip for the same peddler opens.
        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "route cancelled", day.Admin, day.Now.AddHours(2)), day.Peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        var next = new TripId(ids.Next("trip-next"));
        Result<Trip> reopened = new OpenTripService(day.Store).OpenTrip(
            new NewTripCommand(next, day.Peddler, day.Route, day.Admin, day.Now.AddHours(3)));
        Assert.True(reopened.IsSuccess, reopened.Errors.FirstOrDefault()?.Code);
    }

    // ------------------------------------------------------------------
    // Peddler isolation (trips/loads take the peddler; DomainError, never
    // silent).
    // ------------------------------------------------------------------

    [Fact]
    public void Load_ByAnotherPeddler_Rejected_TRIP_PEDDLER_MISMATCH()
    {
        Day day = NewDay();
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        Result<LoadOutcome> crossed = new LoadService(day.Store).Load(new LoadCommand(
            new LoadId(ids.Next("load")), "LD-X", day.Trip, day.StoreLoc,
            new[] { new LoadLine(day.Product, "SKU", "Product", Quantity.Of(1), Money.FromPesos(30m)) },
            day.Admin, day.Now), day.Other);
        Assert.True(crossed.IsFailure);
        Assert.Equal("TRIP_PEDDLER_MISMATCH", crossed.Errors[0].Code);
        Assert.Equal(0, day.Store.Movements.Count(m => m.Kind == MovementKind.TransferIn)); // nothing committed
    }

    [Fact]
    public void Cancel_ByAnotherPeddler_Rejected_TRIP_PEDDLER_MISMATCH()
    {
        Day day = NewDay();
        Result<CancelOutcome> crossed = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "not mine", day.Admin, day.Now.AddHours(1)), day.Other);
        Assert.True(crossed.IsFailure);
        Assert.Equal("TRIP_PEDDLER_MISMATCH", crossed.Errors[0].Code);
    }

    [Fact]
    public void ReadTrip_ByAnotherPeddler_Rejected_TRIP_PEDDLER_MISMATCH()
    {
        Day day = NewDay();
        Result<Trip> crossed = new TripReadService(day.Store).GetTrip(day.Other, day.Trip);
        Assert.True(crossed.IsFailure);
        Assert.Equal("TRIP_PEDDLER_MISMATCH", crossed.Errors[0].Code);

        Result<Trip> own = new TripReadService(day.Store).GetTrip(day.Peddler, day.Trip);
        Assert.True(own.IsSuccess);
        Assert.Equal(day.Trip, own.Value.Id);
    }

    // ------------------------------------------------------------------
    // On-hand from movements: peddler-scoped, no stored stock_qty.
    // ------------------------------------------------------------------

    [Fact]
    public void OnHand_IsPeddlerScoped_AndDerivedFromMovements()
    {
        Day ana = NewDay();

        // Bela: her own trip + her own load of the same product.
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        var bela = new PeddlerId(ids.Next("bela"));
        var belaRoute = new LocationId(ids.Next("bela-route"));
        var tripBela = new TripId(ids.Next("trip-bela"));
        Result<Trip> opened = new OpenTripService(ana.Store).OpenTrip(new NewTripCommand(tripBela, bela, belaRoute, ana.Admin, ana.Now));
        Assert.True(opened.IsSuccess, opened.Errors.FirstOrDefault()?.Code);

        var belaLoad = new LoadId(ids.Next("bela-load"));
        Result<LoadOutcome> loaded = new LoadService(ana.Store).Load(new LoadCommand(
            belaLoad, "LD-BELA", tripBela, ana.StoreLoc,
            new[] { new LoadLine(ana.Product, "SKU", "Product", Quantity.Of(12), Money.FromPesos(30m)) },
            ana.Admin, ana.Now), bela);
        Assert.True(loaded.IsSuccess, loaded.Errors.FirstOrDefault()?.Code);

        var reads = new TripReadService(ana.Store);
        Quantity anaQty = reads.OnHand(ana.Peddler, ana.Product).Value;
        Quantity belaQty = reads.OnHand(bela, ana.Product).Value;

        // Bela's load did NOT touch Ana's on-hand; each is the fold sum over
        // that peddler's OWN locations (movements-derived, no stock_qty).
        Assert.Equal(12, belaQty.Units);
        Assert.Equal(0, anaQty.Units);

        // OnHandAll never lists a product the peddler never loaded/sold.
        Result<IReadOnlyList<OnHandRow>> belaRows = reads.OnHandAll(bela);
        Assert.Single(belaRows.Value);
        Assert.Equal("SKU", belaRows.Value[0].Sku);
        Assert.Equal(12, belaRows.Value[0].Units.Units);

        Result<IReadOnlyList<OnHandRow>> anaRows = reads.OnHandAll(ana.Peddler);
        Assert.Empty(anaRows.Value);

        // The values always equal the fold's StockOnHand (the movements fold).
        Assert.Equal(
            ana.Store.State.StockOnHand(ana.Product, belaRoute).Units,
            reads.OnHand(bela, ana.Product).Value.Units);
    }

    [Fact]
    public void SaleAndLoad_OnCancelledTrip_RejectedAtBuilderAndFold()
    {
        Day day = NewDay();
        LoadOut(day, 10);
        Result<CancelOutcome> cancelled = new CancelTripService(day.Store).Cancel(
            new CancelTripCommand(day.Trip, "cancelled", day.Admin, day.Now.AddHours(2)), day.Peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        // Builder gates.
        var ids = new ScenarioIdSource("trip-lifecycle-test-v1");
        Result<SaleDraft> sale = SaleBuilder.Build(day.Store.Context, new NewSaleCommand(
            new SaleId(ids.Next("sale")), day.Trip, "R-ON-CANCELLED", day.Admin, day.Now.AddHours(3),
            new[] { new NewSaleLine(new SaleLineId(ids.Next("line")), day.Product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(1), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("pay")), PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero) }));
        Assert.True(sale.IsFailure);
        Assert.Equal("TRIP_CANCELLED", sale.Errors[0].Code);

        Result<LoadDraft> load = LoadBuilder.Build(day.Store.Context, new LoadCommand(
            new LoadId(ids.Next("load")), "LD-CANCELLED", day.Trip, day.StoreLoc,
            new[] { new LoadLine(day.Product, "SKU", "Product", Quantity.Of(1), Money.FromPesos(30m)) },
            day.Admin, day.Now));
        Assert.True(load.IsFailure);
        Assert.Equal("TRIP_CANCELLED", load.Errors[0].Code);
    }
}
