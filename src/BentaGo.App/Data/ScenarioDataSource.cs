using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.App.Data;

/// <summary>
/// The BG-004 implementation of the read-only data seam: runs the
/// deterministic two-peddler scenario once over an in-memory ledger store and
/// projects pure read models from the store's folds and documents
/// (bg-004-synthesis §12). The BG-005 swap is one DI registration line.
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

    private static AppSnapshot Build()
    {
        var store = new InMemoryLedgerStore();
        TwoPeddlersScenario.Run(store);
        return Project(store);
    }

    private static AppSnapshot Project(InMemoryLedgerStore store)
    {
        LedgerContext context = store.Context;
        LedgerState state = store.State;

        // The scenario registers Alice's trip first; the peddler session the
        // shell renders is the first peddler's route.
        Trip trip = context.Trips[0];
        LocationId route = trip.Location;

        string tripNumber = $"TRIP-{trip.CreatedUtc:yyyyMMdd}-001";
        string nowText = ManilaDisplay.UtcAsManila(store.Movements[^1].WhenUtc).ToString("HH:mm");

        // Dashboard + summary over Alice's trip's live receipts and documents.
        long transactions = 0;
        Money todaySales = Money.Zero;
        Money discounts = Money.Zero;
        foreach (Sale sale in context.Sales)
        {
            if (sale.Trip != trip.Id)
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
        Money cash = state.TripCash(trip.Id, Fund.PhysicalCash);

        var byMethod = new List<MethodSummary>
        {
            new("Cash", MoneyDisplay.Peso(state.TripCash(trip.Id, Fund.PhysicalCash))),
            new("GCash", MoneyDisplay.Peso(state.TripCash(trip.Id, Fund.Gcash))),
            new("Maya", MoneyDisplay.Peso(state.TripCash(trip.Id, Fund.Maya))),
            new("Bank", MoneyDisplay.Peso(state.TripCash(trip.Id, Fund.Bank))),
        };

        // Stock rows per product on the route (load lines identify the
        // products the peddler carries).
        var products = new List<(ProductId Id, string Sku, string Name)>();
        var seen = new HashSet<ProductId>();
        foreach (StockLoad load in context.Loads)
        {
            if (load.Trip != trip.Id)
            {
                continue;
            }

            foreach (StockLoadLine line in load.Lines)
            {
                if (seen.Add(line.ProductId))
                {
                    products.Add((line.ProductId, line.Sku, line.ProductName));
                }
            }
        }

        foreach (Sale sale in context.Sales)
        {
            if (sale.Trip != trip.Id)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (seen.Add(line.ProductId))
                {
                    products.Add((line.ProductId, line.Sku, line.ProductName));
                }
            }
        }

        int remaining = 0;
        var stockRows = new List<StockRow>();
        var offers = new List<ProductOffer>();
        foreach ((ProductId id, string sku, string name) in products)
        {
            var breakdown = state.Breakdown(id, route);
            remaining += (int)breakdown.Ending;

            // The POS catalog needs a price: the last unit price the scenario
            // sold the product at (fold-derived documents, deterministic).
            Money price = LastUnitPrice(context.Sales, trip.Id, id);
            stockRows.Add(new StockRow(
                sku,
                name,
                breakdown.Loaded.ToString(),
                breakdown.Sold.ToString(),
                (breakdown.Damaged + breakdown.Lost).ToString(),
                breakdown.Returned.ToString(),
                breakdown.Ending.ToString(),
                LastMovementText(store.Movements, id, route),
                breakdown.Ending <= LowStockThreshold));
            offers.Add(new ProductOffer(sku, name, price, (int)breakdown.Ending));
        }

        var session = new SessionHeader(
            $"Hello, {FirstPeddlerName}",
            FirstPeddlerName,
            tripNumber,
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

    private const string FirstPeddlerName = "Alice";

    private static Money LastUnitPrice(IReadOnlyList<Sale> sales, TripId trip, ProductId product)
    {
        Money price = Money.Zero;
        foreach (Sale sale in sales)
        {
            if (sale.Trip != trip)
            {
                continue;
            }

            foreach (SaleLine line in sale.Lines)
            {
                if (line.ProductId == product)
                {
                    price = line.UnitPrice;
                }
            }
        }

        return price.IsZero ? Money.FromPesos(0m) : price;
    }

    private static string LastMovementText(IReadOnlyList<Movement> movements, ProductId product, LocationId location)
    {
        for (int i = movements.Count - 1; i >= 0; i--)
        {
            Movement movement = movements[i];
            (ProductId?, LocationId?) cell = movement switch
            {
                OpeningStock s => (s.ProductId, s.LocationId),
                TransferOut s => (s.ProductId, s.LocationId),
                TransferIn s => (s.ProductId, s.LocationId),
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