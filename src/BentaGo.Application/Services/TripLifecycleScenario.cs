using System.Globalization;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.Application.Services;

/// <summary>
/// The deterministic trip-lifecycle day (BG-005c): the full docs/01 lifecycle
/// open → load → sell → close, the cancel-bearing variant (load → cancel with
/// an atomic load revert), the one-active-trip rejection, the cross-peddler
/// rejection, the post-terminal rejections, and the peddler-scoped on-hand
/// read model — all through the REAL services (OpenTrip/Load/Checkout/Close/
/// Cancel/TripRead) against any <see cref="ILedgerStore"/>, fixed ids and a
/// fixed clock, so the printed report is byte-identical across runs and
/// across the in-memory and SQLite stores (the tool's GOLDEN_IDENT /
/// RESTART / REBUILD checks).
/// </summary>
public static class TripLifecycleScenario
{
    public const string Seed = "trip-lifecycle-v1";
    private static readonly DateTimeOffset FixedUtc = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T1 = FixedUtc.AddHours(1);
    private static readonly DateTimeOffset T2 = FixedUtc.AddHours(2);
    private static readonly DateTimeOffset T3 = FixedUtc.AddHours(3);
    private static readonly DateTimeOffset T4 = FixedUtc.AddHours(4);

    public static ScenarioReport Run(ILedgerStore store)
    {
        var ids = new ScenarioIdSource(Seed);
        var admin = new UserId(ids.Next("user-admin"));
        var ana = new PeddlerId(ids.Next("peddler-ana"));
        var bela = new PeddlerId(ids.Next("peddler-bela"));
        var storeLoc = new LocationId(ids.Next("loc-main-store"));
        var routeAna = new LocationId(ids.Next("loc-ana-route"));
        var routeBela = new LocationId(ids.Next("loc-bela-route"));

        var sardines = new ProductId(ids.Next("product-sardines"));
        var rice = new ProductId(ids.Next("product-rice"));
        var kopi = new ProductId(ids.Next("product-kopi"));

        var openTrip = new OpenTripService(store);
        var load = new LoadService(store);
        var checkout = new CheckoutService(store);
        var close = new CloseService(store);
        var cancel = new CancelTripService(store);

        // Opening stock at the main store (movements, no trip).
        OpenStock(store, ids, "OPEN-SAR", sardines, "Sardines", 100, storeLoc, admin, FixedUtc);
        OpenStock(store, ids, "OPEN-RICE", rice, "Rice", 60, storeLoc, admin, FixedUtc);
        OpenStock(store, ids, "OPEN-KOPI", kopi, "Kopi", 24, storeLoc, admin, FixedUtc);

        // --- Ana: open → load → sell → close (the docs/01 happy lifecycle). ---
        var tripAna = new TripId(ids.Next("trip-ana"));
        Result<Trip> openedAna = openTrip.OpenTrip(new NewTripCommand(tripAna, ana, routeAna, admin, T1));
        Require(openedAna, "open ana");

        Result<LoadOutcome> loadAna1 = load.Load(new LoadCommand(
            new LoadId(ids.Next("load-ana-1")), "LD-ANA-1", tripAna, storeLoc,
            new[] { LoadLineOf(sardines, "Sardines", 30), LoadLineOf(rice, "Rice", 20) }, admin, T1), ana);
        Require(loadAna1, "load ana 1");

        // One-active-trip invariant: a second open for Ana is rejected.
        var secondAna = new TripId(ids.Next("trip-ana-2"));
        Result<Trip> doubleOpen = openTrip.OpenTrip(new NewTripCommand(secondAna, ana, routeAna, admin, T1));
        store.RecordAudit(new AuditEvent(-1, admin, T1, "LANE9",
            $"double-open rejected: {doubleOpen.Errors[0].Code}", null));

        // Peddler isolation: Bela cannot load into Ana's trip.
        Result<LoadOutcome> crossLoad = load.Load(new LoadCommand(
            new LoadId(ids.Next("load-cross")), "LD-CROSS", tripAna, storeLoc,
            new[] { LoadLineOf(kopi, "Kopi", 1) }, admin, T1), bela);
        store.RecordAudit(new AuditEvent(-1, admin, T1, "LANE9",
            $"cross-peddler load rejected: {crossLoad.Errors[0].Code}", null));

        // Ana sells 5 sardines @ 45.00 cash = 225.00.
        Result<CheckoutOutcome> sale = checkout.Checkout(
            new CheckoutKey(ids.Next("key-1")),
            new NewSaleCommand(
                new SaleId(ids.Next("sale-1")), tripAna, "R-TLC-000001", admin, T2,
                new[] { Line(ids, sardines, "Sardines", 5, Money.FromPesos(45m)) },
                new[] { NewPay(ids, PaymentMethod.Cash, Money.FromPesos(225m)) }));
        Require(sale, "sale ana");

        Result<CloseOutcome> closedAna = close.Close(new CloseCommand(
            new CloseId(ids.Next("close-1")), tripAna, Money.FromPesos(225m), null, admin, T3));
        Require(closedAna, "close ana");

        // A post-close load is rejected (terminal trip, docs/01:76 lock).
        Result<LoadOutcome> postCloseLoad = load.Load(new LoadCommand(
            new LoadId(ids.Next("load-after-close")), "LD-AFTER-CLOSE", tripAna, storeLoc,
            new[] { LoadLineOf(sardines, "Sardines", 1) }, admin, T3), ana);
        store.RecordAudit(new AuditEvent(-1, admin, T3, "LANE9",
            $"post-close load rejected: {postCloseLoad.Errors[0].Code}", null));

        // --- Bela: open → load → cancel (the cancel-bearing variant). ---
        var tripBela = new TripId(ids.Next("trip-bela"));
        Require(openTrip.OpenTrip(new NewTripCommand(tripBela, bela, routeBela, admin, T1)), "open bela");

        Require(load.Load(new LoadCommand(
            new LoadId(ids.Next("load-bela-1")), "LD-BELA-1", tripBela, storeLoc,
            new[] { LoadLineOf(kopi, "Kopi", 12) }, admin, T2), bela), "load bela");

        // Cancel before sales with an authorized reason: loads revert in the
        // same transaction, the trip becomes Cancelled (docs/01:45).
        Result<CancelOutcome> cancelled = cancel.Cancel(
            new CancelTripCommand(tripBela, "route cancelled by admin", admin, T3), bela);
        Require(cancelled, "cancel bela");

        // A post-cancel sale is rejected.
        Result<CheckoutOutcome> postCancelSale = checkout.Checkout(
            new CheckoutKey(ids.Next("key-2")),
            new NewSaleCommand(
                new SaleId(ids.Next("sale-after-cancel")), tripBela, "R-TLC-AFTER-CANCEL", admin, T4,
                new[] { Line(ids, kopi, "Kopi", 1, Money.FromPesos(8m)) },
                new[] { NewPay(ids, PaymentMethod.Cash, Money.FromPesos(8m)) }));
        store.RecordAudit(new AuditEvent(-1, admin, T4, "LANE9",
            $"post-cancel sale rejected: {postCancelSale.Errors[0].Code}", null));

        return Render(store);
    }

    /// <summary>
    /// Renders whatever the store currently holds — the ordered movement
    /// stream, audits, trip states, balances, and the peddler-scoped on-hand
    /// read model. Used by the persisted mode to render a reopened/rebuild
    /// store WITHOUT re-running the day (the tool's RESTART/REBUILD checks).
    /// </summary>
    public static ScenarioReport Render(ILedgerStore store)
    {
        var text = new StringBuilder().AppendLine("BentaGo scenario: trip lifecycle").AppendLine("==================================");
        text.AppendLine($"scenario clock (Manila): {ManilaDisplay.DateTime(T2)}");

        foreach (Movement movement in store.Movements)
        {
            text.AppendLine(RenderMovement(movement));
        }

        foreach (AuditEvent audit in store.Audits)
        {
            text.AppendLine($"  [audit] {audit.Message}");
        }

        text.AppendLine();
        text.AppendLine("TRIP STATES");
        text.AppendLine("-----------");
        foreach (Trip trip in store.Context.Trips)
        {
            TripState state = store.State.Trip(trip.Id);
            text.AppendLine($"  trip {trip.Id.Value:N}: {state.Status}" +
                (state.OpenedUtc is { } opened ? $" opened {Iso(opened)}" : "") +
                (state.ClosedUtc is { } closed ? $" closed {Iso(closed)}" : ""));
        }

        text.AppendLine();
        text.AppendLine("BALANCES");
        text.AppendLine("--------");
        text.AppendLine("CASH BY FUND:");
        foreach (Fund fund in Enum.GetValues<Fund>())
        {
            text.AppendLine($"  {fund,-14} {Fmt(store.State.FundsOnHand(fund))}");
        }

        text.AppendLine("STOCK BY CELL (product @ location):");
        foreach ((ProductId product, LocationId location) in StoreCells(store))
        {
            text.AppendLine($"  {product.Value:N} @ {location.Value:N} = {store.State.StockOnHand(product, location).Units}");
        }

        text.AppendLine();
        text.AppendLine("PEDDLER ON-HAND (from movements, peddler-scoped):");
        var reads = new TripReadService(store);
        foreach (PeddlerId peddler in DistinctPeddlers(store))
        {
            Result<IReadOnlyList<OnHandRow>> rows = reads.OnHandAll(peddler);
            text.AppendLine($"  peddler {peddler.Value:N}: " +
                string.Join(", ", rows.Value.Select(r => $"{r.Sku ?? r.Product.Value.ToString("N")}={r.Units.Units}")));
        }

        return new ScenarioReport(text.ToString());
    }

    private static IEnumerable<PeddlerId> DistinctPeddlers(ILedgerStore store)
    {
        var seen = new HashSet<PeddlerId>();
        foreach (Trip trip in store.Context.Trips)
        {
            seen.Add(trip.Peddler);
        }

        return seen.OrderBy(p => p);
    }

    private static void Require<T>(Result<T> result, string what)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"{what} failed: {result.Errors[0].Code}: {result.Errors[0].Message}");
        }
    }

    private static void OpenStock(ILedgerStore store, ScenarioIdSource ids, string number, ProductId product, string name, int qty, LocationId storeLoc, UserId actor, DateTimeOffset when)
    {
        var loadId = new LoadId(ids.Next($"opening-{number}"));
        store.RegisterLoad(new StockLoad(loadId, number, default, Array.Empty<StockLoadLine>(), actor, when));
        store.Bind();
        store.Append(new[] { Moves.OpeningStock(0, when, actor, new LoadRef(loadId), product, storeLoc, Quantity.Of(qty)) });
    }

    private static LoadLine LoadLineOf(ProductId product, string name, int qty) =>
        new(product, name, name, Quantity.Of(qty), Money.FromPesos(0m));

    private static NewSaleLine Line(ScenarioIdSource ids, ProductId product, string name, int qty, Money price) =>
        new(new SaleLineId(ids.Next($"line-{name}-{qty}")), product, name, name, price, Quantity.Of(qty), Money.Zero);

    private static NewPaymentLine NewPay(ScenarioIdSource ids, PaymentMethod method, Money amount) =>
        new(new PaymentLineId(ids.Next($"pay-{method}-{amount.Centavos}")), method, "", amount, amount, Money.Zero);

    private static IEnumerable<(ProductId, LocationId)> StoreCells(ILedgerStore store)
    {
        var seen = new HashSet<string>();
        foreach (Movement m in store.Movements)
        {
            (ProductId?, LocationId?) cell = m switch
            {
                OpeningStock s => (s.ProductId, s.LocationId),
                TransferOut t => (t.ProductId, t.LocationId),
                TransferIn t => (t.ProductId, t.LocationId),
                SaleIssue s => (s.ProductId, s.LocationId),
                SaleIssueRevoke s => (s.ProductId, s.LocationId),
                ReturnToStock r => (r.ProductId, r.LocationId),
                Damage d => (d.ProductId, d.LocationId),
                Loss l => (l.ProductId, l.LocationId),
                AdjustmentIncrease a => (a.ProductId, a.LocationId),
                AdjustmentDecrease a => (a.ProductId, a.LocationId),
                CountObserved c => (c.ProductId, c.LocationId),
                _ => (null, null),
            };

            if (cell.Item1 is { } product && cell.Item2 is { } location && seen.Add($"{product}:{location}"))
            {
                yield return (product, location);
            }
        }
    }

    private static string Fmt(Money money) =>
        money.ToPesos().ToString("0.00", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset when) =>
        when.ToString("O", CultureInfo.InvariantCulture);

    private static string RenderMovement(Movement m) =>
        $"{m.Sequence,4} {m.Kind,-18} {Describe(m)}";

    private static string Describe(Movement m) =>
        m switch
        {
            OpeningStock s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} +{s.Quantity.Units}",
            TransferOut t => $"{t.ProductId.Value:N} @ {t.LocationId.Value:N} -{t.Quantity.Units}",
            TransferIn t => $"{t.ProductId.Value:N} @ {t.LocationId.Value:N} +{t.Quantity.Units}",
            SaleIssue s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} -{s.Quantity.Units}",
            SaleIssueRevoke s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} +{s.Quantity.Units} (reverses {s.Reverses})",
            ReturnToStock r => $"{r.ProductId.Value:N} @ {r.LocationId.Value:N} +{r.Quantity.Units} (reverses {r.Reverses})",
            Receipt r => $"{r.Method} +{Fmt(r.Amount)}",
            ReceiptRefund r => $"{r.Method} -{Fmt(r.Amount)} (reverses {r.Reverses})",
            ReceiptRevoke r => $"{r.Method} -{Fmt(r.Amount)} (reverses {r.Reverses})",
            CountObserved c => $"{c.ProductId.Value:N} @ {c.LocationId.Value:N} observed {c.ObservedQuantity.Units}",
            Damage d => $"{d.ProductId.Value:N} @ {d.LocationId.Value:N} -{d.Quantity.Units} ({d.Reason})",
            Loss l => $"{l.ProductId.Value:N} @ {l.LocationId.Value:N} -{l.Quantity.Units} ({l.Reason})",
            AdjustmentIncrease a => $"{a.ProductId.Value:N} @ {a.LocationId.Value:N} +{a.Quantity.Units} ({a.Reason})",
            AdjustmentDecrease a => $"{a.ProductId.Value:N} @ {a.LocationId.Value:N} -{a.Quantity.Units} ({a.Reason})",
            TripClose c => $"trip {c.TripId.Value:N}",
            TripReopen c => $"trip {c.TripId.Value:N} ({c.Reason})",
            TripCancel c => $"trip {c.TripId.Value:N} cancelled ({c.Reason})",
            _ => m.Kind.ToString(),
        };
}