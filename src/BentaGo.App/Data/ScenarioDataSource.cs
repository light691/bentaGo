using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.App.Data;

/// <summary>
/// The read-only data seam implementation for the store-POS pivot: a MINIMAL
/// deterministic day-lifecycle seed over an in-memory ledger store — one
/// Manila trading day opened with a float, goods received into the single
/// warehouse, and two cash sales — projected into pure read models from the
/// store's fold and documents (bg-004-synthesis §12, retargeted per
/// docs/decisions/0004). Same seed, byte-identical re-runs.
/// </summary>
public sealed class ScenarioDataSource : IAppDataSource
{
    /// <summary>Presentation threshold for the low-stock flag (units on hand).</summary>
    internal const int LowStockThreshold = 5;

    private readonly Lazy<AppSnapshot> _snapshot;

    public ScenarioDataSource()
    {
        _snapshot = new Lazy<AppSnapshot>(Build);
    }

    public AppSnapshot Current => _snapshot.Value;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    // Deterministic ids/shapes (no ScenarioIdSource dependency here — plain
    // fixed Guids keep the seed readable).
    private static readonly UserId SeedUser = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly DateTimeOffset SeedWhen = new(2026, 1, 15, 1, 30, 0, TimeSpan.Zero); // 09:30 Manila
    private static readonly DayKey SeedDay = new("2026-01-15");

    private sealed record SeedProduct(ProductId Id, string Sku, string Name, int ReceivedUnits, Money UnitPrice, int SoldUnits);

    private static readonly SeedProduct[] SeedProducts =
    [
        new(new ProductId(Guid.Parse("00000000-0000-0000-0000-00000000d001")), "SARDINES", "Sardines 155g", 40, Money.FromPesos(45m), 2),
        new(new ProductId(Guid.Parse("00000000-0000-0000-0000-00000000d002")), "KOPI-3IN1", "Kopi 3in1 20g", 60, Money.FromPesos(8m), 3),
        new(new ProductId(Guid.Parse("00000000-0000-0000-0000-00000000d003")), "NOODLES", "Instant noodles 60g", 4, Money.FromPesos(15m), 0),
    ];

    private static AppSnapshot Build()
    {
        var store = new InMemoryLedgerStore();
        Seed(store);
        return Project(store);
    }

    /// <summary>The minimal deterministic day: open, receive, sell twice.</summary>
    private static void Seed(InMemoryLedgerStore store)
    {
        // 1. Open the trading day with an opening float.
        store.RegisterDayOpen(new DayOpen(
            new DayId(Guid.Parse("00000000-0000-0000-0000-00000000e001")),
            SeedDay, Money.FromPesos(500m).Centavos, SeedUser, SeedWhen));
        store.Bind();

        // 2. Goods received into the single warehouse (one movement per product).
        var goodsId = new GoodsId(Guid.Parse("00000000-0000-0000-0000-00000000e002"));
        var movements = new List<Movement>(SeedProducts.Length);
        long seq = 0;
        foreach (SeedProduct product in SeedProducts)
        {
            movements.Add(Moves.GoodsReceived(
                ++seq, SeedWhen, SeedUser, new GoodsRef(goodsId),
                product.Id, StoreLocations.Warehouse, Quantity.Of(product.ReceivedUnits)));
        }

        store.Append(movements);
        store.Bind();

        // 3. Two cash sales through the real sale builder (deterministic ids).
        SaleLineId Line(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000f00{n}"));
        PaymentLineId Pay(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000f10{n}"));
        SaleId Sale(int n) => new(Guid.Parse($"00000000-0000-0000-0000-00000000f20{n}"));

        for (int i = 0; i < 2; i++)
        {
            SeedProduct product = SeedProducts[i];
            Money lineTotal = product.UnitPrice.Times(product.SoldUnits);
            NewSaleCommand cmd = new(
                Sale(i + 1),
                SeedDay,
                $"R-00000{i + 1}",
                SeedUser,
                SeedWhen.AddMinutes(10 + i),
                [new NewSaleLine(Line(i + 1), product.Id, product.Sku, product.Name, product.UnitPrice, Quantity.Of(product.SoldUnits), Money.Zero)],
                [new NewPaymentLine(Pay(i + 1), PaymentMethod.Cash, "", lineTotal, lineTotal, Money.Zero)]);

            Result<SaleDraft> draft = SaleBuilder.Build(store.Context, cmd);
            if (draft.IsFailure)
            {
                throw new InvalidOperationException($"scenario seed sale failed: {draft.Errors[0].Code}");
            }

            store.RegisterSale(draft.Value.Document);
            store.Bind();
            store.Append(draft.Value.Movements);
            store.Bind();
        }
    }

    private static AppSnapshot Project(InMemoryLedgerStore store)
    {
        LedgerContext context = store.Context;
        LedgerState state = store.State;
        LocationId warehouse = StoreLocations.Warehouse;

        DateTimeOffset lastWhen = store.Movements.Count > 0
            ? store.Movements[^1].WhenUtc
            : SeedWhen;
        string nowText = ManilaDisplay.UtcAsManila(lastWhen).ToString("HH:mm");

        // Dashboard + summary over the day's live receipts and documents.
        long transactions = 0;
        Money todaySales = Money.Zero;
        Money discounts = Money.Zero;
        foreach (Sale sale in context.Sales)
        {
            if (sale.Day != SeedDay)
            {
                continue;
            }

            if (state.Sale(sale.Id).Status == SaleStatus.Voided)
            {
                continue;
            }

            transactions++;
            todaySales = todaySales.Plus(sale.Total);
            foreach (SaleLine line in sale.Lines)
            {
                discounts = discounts.Plus(line.Discount);
            }
        }

        Money gross = todaySales;
        Money net = gross.Minus(discounts);
        Money cash = state.FundsOnHand(Fund.PhysicalCash);

        var byMethod = new List<MethodSummary>
        {
            new("Cash", MoneyDisplay.Peso(state.FundsOnHand(Fund.PhysicalCash))),
            new("GCash", MoneyDisplay.Peso(state.FundsOnHand(Fund.Gcash))),
            new("Maya", MoneyDisplay.Peso(state.FundsOnHand(Fund.Maya))),
            new("Bank", MoneyDisplay.Peso(state.FundsOnHand(Fund.Bank))),
        };

        // Stock rows per seeded product: sold from the day's own sale lines,
        // on hand from the movement fold at the warehouse.
        int remaining = 0;
        var stockRows = new List<StockRow>();
        var offers = new List<ProductOffer>();
        foreach (SeedProduct product in SeedProducts)
        {
            int onHand = state.StockOnHand(product.Id, warehouse).Units;
            remaining += onHand;

            stockRows.Add(new StockRow(
                product.Sku,
                product.Name,
                product.ReceivedUnits.ToString(),
                product.SoldUnits.ToString(),
                "0",
                "0",
                onHand.ToString(),
                LastMovementText(store.Movements, product.Id, warehouse),
                onHand <= LowStockThreshold));
            offers.Add(new ProductOffer(product.Sku, product.Name, product.UnitPrice, onHand));
        }

        var session = new SessionHeader(
            $"Welcome — day {SeedDay.Value}",
            "Store",
            $"DAY {SeedDay.Value}",
            IsOnline: true,
            nowText,
            PreviousDateTripOpen: false);

        var dashboard = new DashboardSummary(
            MoneyDisplay.Peso(todaySales),
            transactions.ToString(),
            MoneyDisplay.Peso(cash),
            remaining.ToString());

        var summary = new ReconciliationSummary(byMethod, gross, discounts, net, cash);

        return new AppSnapshot(session, dashboard, stockRows, offers, summary);
    }

    private static string LastMovementText(IReadOnlyList<Movement> movements, ProductId product, LocationId location)
    {
        for (int i = movements.Count - 1; i >= 0; i--)
        {
            Movement movement = movements[i];
            (ProductId?, LocationId?) cell = movement switch
            {
                GoodsReceived s => (s.ProductId, s.LocationId),
                SaleIssue s => (s.ProductId, s.LocationId),
                SaleIssueRevoke s => (s.ProductId, s.LocationId),
                ReturnToStock s => (s.ProductId, s.LocationId),
                Damage s => (s.ProductId, s.LocationId),
                Loss s => (s.ProductId, s.LocationId),
                AdjustmentIncrease s => (s.ProductId, s.LocationId),
                AdjustmentDecrease s => (s.ProductId, s.LocationId),
                CountObserved s => (s.ProductId, s.LocationId),
                _ => (null, null),
            };

            if (cell.Item1 == product && cell.Item2 == location)
            {
                return ManilaDisplay.UtcAsManila(movement.WhenUtc).ToString("dd MMM yyyy HH:mm");
            }
        }

        return "-";
    }
}
