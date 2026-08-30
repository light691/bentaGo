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
/// The rendered scenario report: the complete ordered business event
/// sequence (documents at their sequence positions) followed by the exact
/// expected cash and stock balances per peddler. Pure rendering — both the
/// tool and the tests share this single source.
/// </summary>
public sealed record ScenarioReport(string Text);

/// <summary>
/// The deterministic two-peddler business day: fixed ids, fixed clock, same
/// append order — the printed report is byte-identical across runs (lane 10)
/// and the balances are produced by the same folds the app uses.
///
/// The single LANE9 row is a scenario diagnostic recording a REJECTED
/// post-close sale — not a user action on a terminal, so Terminal/Reason
/// stay null (F12: batch/diagnostic audits keep null).
/// </summary>
public static class TwoPeddlersScenario
{
    public const string Seed = "two-peddlers-v1";
    private static readonly DateTimeOffset FixedUtc = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset T1 = FixedUtc.AddHours(1);
    private static readonly DateTimeOffset T2 = FixedUtc.AddHours(2);
    private static readonly DateTimeOffset T3 = FixedUtc.AddHours(3);
    private static readonly DateTimeOffset T4 = FixedUtc.AddHours(4);
    private static readonly DateTimeOffset T5 = FixedUtc.AddHours(5);
    private static readonly DateTimeOffset T6 = FixedUtc.AddHours(6);
    private static readonly DateTimeOffset T7 = FixedUtc.AddHours(7);

    public static ScenarioReport Run(ILedgerStore store)
    {
        var ids = new ScenarioIdSource(Seed);
        var userId = new UserId(ids.Next("user-alice"));
        var peddlerAlice = new PeddlerId(ids.Next("peddler-alice"));
        var peddlerBob = new PeddlerId(ids.Next("peddler-bob"));
        var storeLoc = new LocationId(ids.Next("loc-main-store"));
        var routeAlice = new LocationId(ids.Next("loc-alice-route"));
        var routeBob = new LocationId(ids.Next("loc-bob-route"));

        var sardines = new ProductId(ids.Next("product-sardines"));
        var rice = new ProductId(ids.Next("product-rice"));
        var kopi = new ProductId(ids.Next("product-kopi"));

        var tripAlice = new TripId(ids.Next("trip-alice"));
        var tripBob = new TripId(ids.Next("trip-bob"));

        var memory = store;

        // Trips.
        memory.RegisterTrip(new Trip(tripAlice, peddlerAlice, routeAlice, FixedUtc, userId));
        memory.RegisterTrip(new Trip(tripBob, peddlerBob, routeBob, FixedUtc, userId));
        memory.Bind();

        // Opening stock into the main store: pure OpeningStock movements
        // against opening load documents (no trip association).
        OpenStock(memory, ids, "OPEN-SAR", sardines, "Sardines", 100, storeLoc, userId, FixedUtc);
        OpenStock(memory, ids, "OPEN-RICE", rice, "Rice", 60, storeLoc, userId, FixedUtc);
        OpenStock(memory, ids, "OPEN-KOPI", kopi, "Kopi", 24, storeLoc, userId, FixedUtc);

        // Loads out to the peddlers (these open the trips: first activity).
        LoadOut(memory, ids, "LD-A1", tripAlice, storeLoc, routeAlice, sardines, "Sardines", 40, userId, T1);
        LoadOut(memory, ids, "LD-A2", tripAlice, storeLoc, routeAlice, rice, "Rice", 25, userId, T1);
        LoadOut(memory, ids, "LD-A3", tripAlice, storeLoc, routeAlice, kopi, "Kopi", 24, userId, T1);
        LoadOut(memory, ids, "LD-B1", tripBob, storeLoc, routeBob, sardines, "Sardines", 35, userId, T1);

        // Alice: cash sale 5 sardines @ 45.00 = 225.00.
        Sale sale1 = SaleOf(memory, ids, "R-000001", tripAlice, peddlerAlice, routeAlice, userId, T2,
            new[] { Line(ids, sardines, "Sardines", 5, Money.FromPesos(45m)) },
            new[] { NewPay(ids, PaymentMethod.Cash, Money.FromPesos(225m), cash: true) });

        // Alice: split sale 20 kopi @ 8.00 = 160.00 (cash 80 + GCash 80).
        Sale sale2 = SaleOf(memory, ids, "R-000002", tripAlice, peddlerAlice, routeAlice, userId, T2,
            new[] { Line(ids, kopi, "Kopi", 20, Money.FromPesos(8m)) },
            new[]
            {
                NewPay(ids, PaymentMethod.Cash, Money.FromPesos(80m), cash: true),
                NewPay(ids, PaymentMethod.Gcash, Money.FromPesos(80m), cash: false),
            });

        // Bob: credit sale 10 sardines @ 45.00 = 450.00 utang to Aling Nena.
        Sale sale3 = SaleOf(memory, ids, "R-000003", tripBob, peddlerBob, routeBob, userId, T3,
            new[] { Line(ids, sardines, "Sardines", 10, Money.FromPesos(45m)) },
            new[] { NewPay(ids, PaymentMethod.Credit, Money.FromPesos(450m), cash: false, "Aling Nena") });

        // Alice: void the kopi sale (customer changed their mind) — approved
        // by the operator (BG-007 approval policy).
        {
            var voidId = new VoidId(ids.Next("void-1"));
            var draft = VoidBuilder.Build(memory.Context, new VoidCommand(
                voidId, sale2.Id, "customer changed mind", userId, T3, ApprovedBy: userId));
            memory.RegisterVoid(draft.Value.Document);
            memory.Bind();
            memory.Append(draft.Value.Movements);
        }

        // Alice: partial return on sale 1 (1 sardine, cash refund 45.00).
        {
            var returnId = new ReturnId(ids.Next("return-1"));
            var draft = ReturnBuilder.Build(memory.Context, new ReturnCommand(
                returnId, sale1.Id,
                new[]
                {
                    new ReturnLineCommand(sale1.Lines[0].Id, Quantity.Of(1), "damaged can", PaymentMethod.Cash, Money.FromPesos(45m)),
                },
                userId, T4));
            memory.RegisterReturn(draft.Value.Document);
            memory.Bind();
            memory.Append(draft.Value.Movements);
        }

        // Alice: physical count of sardines on the route.
        {
            var countId = new CountId(ids.Next("count-1"));
            var draft = CountBuilder.Build(memory.Context, new CountCommand(
                countId, routeAlice,
                new[] { new CountLineCommand(sardines, Quantity.Of(38), null) },
                userId, T5));
            memory.RegisterCount(draft.Value.Document);
            memory.Bind();
            memory.Append(draft.Value.Movements);
        }

        // Bob: damage 1 sardine in transit (approved by the operator).
        {
            var adjId = new AdjustmentId(ids.Next("damage-1"));
            var draft = AdjustmentBuilder.Build(memory.Context, new AdjustmentCommand(
                adjId, tripBob, sardines, routeBob, AdjustmentKind.Damage,
                Quantity.Of(1), "crushed while peddling", userId, T5, ApprovedBy: userId));
            memory.Append(draft.Value.Movements);
        }

        // Alice closes the trip at counted cash 180.00: 225 (cash sale 1) + 80
        // (cash share of the split sale) - 80 (void revoked the cash receipt)
        // - 45 (refund) = 180. The close reads the trip's own cash, not Bob's.
        {
            var closeId = new CloseId(ids.Next("close-1"));
            var draft = CloseBuilder.Build(memory.Context, new CloseCommand(
                closeId, tripAlice, Money.FromPesos(180m), null, userId, T6));
            memory.RegisterClose(draft.Value.Document);
            memory.Bind();
            memory.Append(draft.Value.Movements);
        }

        // Lane 9 proof: a sale on the closed trip is rejected before any movement exists.
        {
            var attemptId = new SaleId(ids.Next("attempt-after-close"));
            var result = SaleBuilder.Build(memory.Context, new NewSaleCommand(
                attemptId, tripAlice, "R-ATTEMPT", userId, T6,
                new[] { Line(ids, sardines, "Sardines", 1, Money.FromPesos(45m)) },
                new[] { NewPay(ids, PaymentMethod.Cash, Money.FromPesos(45m), cash: true) }));
            memory.RecordAudit(new AuditEvent(-1, userId, T6, "LANE9", $"post-close sale rejected: {result.Errors[0].Code}", null));
        }

        // Authorized reopen (admin reason) and a successful sale afterwards.
        {
            var draft = ReopenBuilder.Build(memory.Context, new ReopenCommand(tripAlice, "weekly reconciliation", userId, T6));
            memory.Append(draft.Value.Movements);

            Sale saleAfter = SaleOf(memory, ids, "R-000004", tripAlice, peddlerAlice, routeAlice, userId, T7,
                new[] { Line(ids, rice, "Rice", 2, Money.FromPesos(52m)) },
                new[] { NewPay(ids, PaymentMethod.Cash, Money.FromPesos(104m), cash: true) });
        }

        return Render(memory);
    }

    /// <summary>
    /// Renders whatever the store currently holds: the ordered movement
    /// stream, audits, and balances. Used by the persisted mode to render a
    /// reopened/rebuild store WITHOUT re-running the day (BG-003 lanes 6/10).
    /// </summary>
    public static ScenarioReport Render(ILedgerStore store)
    {
        // Money columns are formatted invariant (comma-decimal hosts print the
        // same golden), and the clock is shown in Asia/Manila per the display
        // contract while storage stays UTC.
        string text0 = new StringBuilder().AppendLine("BentaGo scenario: two peddlers").AppendLine("=============================").ToString();
        var text = new StringBuilder(text0);
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

        return new ScenarioReport(text.ToString());
    }

    private static string Fmt(Money money) =>
        money.ToPesos().ToString("0.00", CultureInfo.InvariantCulture);

    private static void OpenStock(ILedgerStore store, ScenarioIdSource ids, string number, ProductId product, string name, int qty, LocationId storeLoc, UserId actor, DateTimeOffset when)
    {
        var loadId = new LoadId(ids.Next($"opening-{number}"));
        store.RegisterLoad(new StockLoad(loadId, number, default, Array.Empty<StockLoadLine>(), actor, when));
        store.Bind();
        store.Append(new[] { Moves.OpeningStock(0, when, actor, new LoadRef(loadId), product, storeLoc, Quantity.Of(qty)) });
    }

    private static void LoadOut(ILedgerStore store, ScenarioIdSource ids, string number, TripId trip, LocationId from, LocationId to, ProductId product, string name, int qty, UserId actor, DateTimeOffset when)
    {
        var loadId = new LoadId(ids.Next($"load-{number}"));
        var draft = LoadBuilder.Build(store.Context, new LoadCommand(
            loadId, number, trip, from,
            new[] { new LoadLine(product, name, name, Quantity.Of(qty), Money.FromPesos(0m)) }, actor, when));
        store.RegisterLoad(draft.Value.Document);
        store.Bind();
        store.Append(draft.Value.Movements);
    }

    private static Sale SaleOf(ILedgerStore store, ScenarioIdSource ids, string receipt, TripId trip, PeddlerId peddler, LocationId location, UserId actor, DateTimeOffset when, NewSaleLine[] lines, NewPaymentLine[] payments)
    {
        var saleId = new SaleId(ids.Next($"sale-{receipt}"));
        var draft = SaleBuilder.Build(store.Context, new NewSaleCommand(saleId, trip, receipt, actor, when, lines, payments));
        store.RegisterSale(draft.Value.Document);
        store.Bind();
        store.Append(draft.Value.Movements);
        return draft.Value.Document;
    }

    private static NewSaleLine Line(ScenarioIdSource ids, ProductId product, string name, int qty, Money price) =>
        new(new SaleLineId(ids.Next($"line-{name}-{qty}")), product, name, name, price, Quantity.Of(qty), Money.Zero);

    private static NewPaymentLine NewPay(ScenarioIdSource ids, PaymentMethod method, Money amount, bool cash, string reference = "") =>
        cash
            ? new NewPaymentLine(new PaymentLineId(ids.Next($"pay-{method}-{amount.Centavos}")), method, reference, amount, amount, Money.Zero)
            : new NewPaymentLine(new PaymentLineId(ids.Next($"pay-{method}-{amount.Centavos}")), method, reference, amount, Money.Zero, Money.Zero);

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
            _ => m.Kind.ToString(),
        };
}
