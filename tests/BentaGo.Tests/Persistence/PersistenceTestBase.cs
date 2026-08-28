using System.IO;
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
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// Shared harness for the Persistence buckets: real SQLite files in fresh
/// temp roots, a host per root, and deterministic seeds. All buckets run
/// with `dotnet test -c Release --filter Persistence`.
/// </summary>
public abstract class PersistenceTestBase : IDisposable
{
    private readonly List<string> _roots = new();

    protected string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "bentago-persist-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            TryDeleteDirectory(root);
        }
    }

    protected static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    protected static string DbPath(string root) => Path.Combine(root, "data", "bentago.db");

    protected ServiceProvider NewHost(string root) => BentaGoHost.BuildForTests(root);

    protected async Task<SqliteLedgerStore> NewMigratedStoreAsync(string root)
    {
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        return host.GetRequiredService<SqliteLedgerStore>();
    }

    /// <summary>
    /// Seeds a sellable day with known ids: trip + opening stock + a load to
    /// the route.
    /// </summary>
    protected static (TripId Trip, ProductId Product, LocationId Route, LocationId Store, UserId User, PeddlerId Peddler) Known()
    {
        var ids = new ScenarioIdSource("persist-known-v1");
        return (
            new TripId(ids.Next("trip")),
            new ProductId(ids.Next("product")),
            new LocationId(ids.Next("route")),
            new LocationId(ids.Next("store")),
            new UserId(ids.Next("user")),
            new PeddlerId(ids.Next("peddler")));
    }

    /// <summary>Registers the trip, opens stock, loads to the route on the given store.</summary>
    protected static void SeedDay(
        SqliteLedgerStore store,
        TripId trip,
        ProductId product,
        LocationId route,
        LocationId storeLoc,
        UserId user,
        PeddlerId peddler,
        int opening = 100,
        int loaded = 50)
    {
        var ids = new ScenarioIdSource("persist-seed-v1");

        store.RegisterTrip(new Trip(trip, peddler, route, SeedWhen, user));

        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "P-OPEN", trip, Array.Empty<StockLoadLine>(), user, SeedWhen));
        store.Bind();
        Result<AppendReceipt> open = store.Append(new[]
        {
            Moves.OpeningStock(0, SeedWhen, user, new LoadRef(openId), product, storeLoc, Quantity.Of(opening)),
        });
        Assert.True(open.IsSuccess, open.Errors.FirstOrDefault()?.Code ?? "opening stock failed");

        var loadId = new LoadId(ids.Next("load"));
        Result<LoadDraft> load = LoadBuilder.Build(store.Context, new LoadCommand(
            loadId, "P-LOAD", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(loaded), Money.FromPesos(30m)) }, user, SeedWhen));
        Assert.True(load.IsSuccess, load.Errors.FirstOrDefault()?.Code ?? "load build failed");
        store.RegisterLoad(load.Value.Document);
        store.Bind();
        Result<AppendReceipt> loadAppend = store.Append(load.Value.Movements);
        Assert.True(loadAppend.IsSuccess, loadAppend.Errors.FirstOrDefault()?.Code ?? "load append failed");
    }

    protected static DateTimeOffset SeedWhen { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    protected static NewSaleCommand OneUnitSale(TripId trip, ProductId product, string receipt, UserId user, int qty = 1)
    {
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        return new NewSaleCommand(
            new SaleId(Guid.NewGuid()), trip, receipt, user, SeedWhen.AddHours(1),
            new[]
            {
                new NewSaleLine(lineId, product, "SKU", "Product", Money.FromPesos(45m), Quantity.Of(qty), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "", Money.FromPesos(45m * qty), Money.FromPesos(45m * qty), Money.Zero),
            });
    }
}
