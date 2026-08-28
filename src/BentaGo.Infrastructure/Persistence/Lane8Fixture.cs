using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Deterministic fixture for lane 8 (power loss): a database with exactly ONE
/// sellable unit at the route, owned by fixed ids derived from a fixed seed.
/// The crash-worker sells that unit inside a transaction delayed before
/// COMMIT; the parent kills it mid-transaction, so recovery must yield the
/// full transaction or none of it.
/// </summary>
public static class Lane8Fixture
{
    private static readonly ScenarioIdSource Ids = new("lane8-v1");

    public static TripId Trip { get; } = new(Ids.Next("trip"));

    public static PeddlerId Peddler { get; } = new(Ids.Next("peddler"));

    public static LocationId Store { get; } = new(Ids.Next("store"));

    public static LocationId Route { get; } = new(Ids.Next("route"));

    public static ProductId Sardines { get; } = new(Ids.Next("sardines"));

    public static UserId User { get; } = new(Ids.Next("user"));

    public static DateTimeOffset When { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seeds a fresh data root with one trip and ONE unit of stock at the
    /// route (via the store, so the fold/guards agree with the file). Returns
    /// the database path.
    /// </summary>
    public static string Seed(string dataRoot)
    {
        var paths = new DataPathProvider(dataRoot);
        paths.EnsureCreated();
        string dbPath = System.IO.Path.Combine(paths.DataDir, "bentago.db");

        var factory = new BentaGoDbContextFactory($"Data Source={dbPath};Pooling=False");
        IDbContextFactory<BentaGoDbContext> contextFactory = factory;
        using (var context = contextFactory.CreateDbContext())
        {
            context.Database.Migrate();
        }

        var store = new SqliteLedgerStore(paths, new DebugOptions());
        store.RegisterTrip(new Trip(Trip, Peddler, Route, When, User));
        var openLoad = new LoadId(Ids.Next("open"));
        store.RegisterLoad(new StockLoad(openLoad, "L8-OPEN", Trip, Array.Empty<StockLoadLine>(), User, When));
        store.Bind();
        Result<AppendReceipt> receipt = store.Append(new[]
        {
            Moves.OpeningStock(0, When, User, new LoadRef(openLoad), Sardines, Route, Quantity.Of(1)),
        });
        if (receipt.IsFailure)
        {
            throw new InvalidOperationException($"lane8 fixture seed failed: {receipt.Errors[0].Code}");
        }

        store.Close();
        return dbPath;
    }

    /// <summary>
    /// Builds the draft that sells the LAST unit (one sardine, cash).
    /// </summary>
    public static (Sale Document, IReadOnlyList<Movement> Movements) BuildSale(ILedgerStore store)
    {
        var saleId = new SaleId(Ids.Next("sale"));
        var lineId = new SaleLineId(Ids.Next("line"));
        var paymentId = new PaymentLineId(Ids.Next("pay"));
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            saleId, Trip, "R-L8", User, When,
            new[]
            {
                new NewSaleLine(lineId, Sardines, "SARD", "Sardines", Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero),
            }));
        if (draft.IsFailure)
        {
            throw new InvalidOperationException(
                $"lane8 sale build failed: {string.Join("; ", draft.Errors.Select(e => e.Code))}");
        }

        return (draft.Value.Document, draft.Value.Movements);
    }
}
