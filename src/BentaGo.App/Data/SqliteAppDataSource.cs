using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.App.Data;

/// <summary>
/// The read-only data seam over the LIVE SQLite ledger (BG-P1d): the same
/// fold-derived projection the BG-002 scenario served, but sourced from the
/// registered <see cref="ILedgerStore"/> (the long-lived SqliteLedgerStore
/// connection the transactional write path uses) and the catalog
/// <see cref="IProductStore"/> — nothing is seeded, nothing is in-memory.
/// Stock rows come from the movement fold at the single warehouse; the
/// session header reflects the day lifecycle the builders enforce.
/// </summary>
public sealed class SqliteAppDataSource : IAppDataSource
{
    /// <summary>Presentation threshold for the low-stock flag (units on hand).</summary>
    internal const int LowStockThreshold = 5;

    private readonly ILedgerStore _store;
    private readonly IProductStore _products;
    private readonly Func<DateTimeOffset> _clock;

    public SqliteAppDataSource(ILedgerStore store, IProductStore products, Func<DateTimeOffset> clock)
    {
        _store = store;
        _products = products;
        _clock = clock;
    }

    /// <summary>
    /// A fresh immutable projection per read (fold-derived from the live
    /// store); screens bind once per navigation, so the read cost is one
    /// catalog query plus an in-memory fold walk.
    /// </summary>
    public AppSnapshot Current => Project();

    /// <summary>Repository change notifications (post-checkout, post-close); screens refresh on navigation.</summary>
#pragma warning disable CS0067 // Raised by future write-path hooks (BG-P2 receiving); screens refresh on navigation today.
    public event EventHandler? Changed;
#pragma warning restore CS0067

    private AppSnapshot Project()
    {
        LedgerState state = _store.State;
        LocationId warehouse = StoreLocations.Warehouse;

        DateTimeOffset now = _clock();
        DayKey today = DayKey.FromUtc(now);

        // Day status from the same lifecycle state the builders gate on.
        string dayStatus = state.Day(today) switch
        {
            DayLifecycleState.Open => "Open - selling",
            DayLifecycleState.Closed => "Closed - reopen to trade",
            _ => "No day open yet",
        };

        var session = new SessionHeader(
            Greeting: "Welcome",
            DayText: $"Day {today.Value}",
            DayStatus: dayStatus,
            NowText: ManilaDisplay.UtcAsManila(now).ToString("HH:mm"));

        // Catalog products drive the rows; on-hand folds from the persisted
        // movements at the warehouse. The EF catalog read runs off the
        // dispatcher so its async continuation cannot marshal onto the UI
        // thread (constructor-time projection).
        IReadOnlyList<Product> catalog =
            Task.Run(async () => await _products.GetAllAsync()).GetAwaiter().GetResult();

        var stockRows = new List<StockRow>(catalog.Count);
        var offers = new List<ProductOffer>(catalog.Count);
        foreach (Product product in catalog)
        {
            int onHand = state.StockOnHand(product.Id, warehouse).Units;
            stockRows.Add(new StockRow(
                product.Sku,
                product.Name,
                onHand.ToString(),
                LastMovementText(_store.Movements, product.Id, warehouse),
                onHand <= LowStockThreshold));
            offers.Add(new ProductOffer(product.Sku, product.Name, product.RetailPrice, onHand));
        }

        return new AppSnapshot(session, stockRows, offers);
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
