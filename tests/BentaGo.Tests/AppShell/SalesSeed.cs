using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// BG-006b shared seed for the AppShell tests that drive the REAL
/// session-backed sales flow (DialogResultTests, SalesUxTests, STA probes):
/// a migrated root, one open trip with 50 units on the route, a
/// peddler-linked signed-in user, and the catalog products the tests scan.
/// </summary>
internal static class SalesSeed
{
    public const string SardinesSku = "SARDINES";
    public const string KopiSku = "KOPI";
    public const string Barcode = "4801234567890";
    public const string SellerUsername = "seller";
    public const string SellerPassword = "seller";

    public static void Seed(ServiceProvider services, bool signIn = true)
    {
        EnsureMigrated(services);
        var store = services.GetRequiredService<SqliteLedgerStore>();
        var ids = new ScenarioIdSource("appshell-sales-v1");

        TripId trip = new(ids.Next("trip"));
        ProductId product = new(ids.Next("product"));
        ProductId kopi = new(ids.Next("kopi"));
        LocationId route = new(ids.Next("route"));
        LocationId storeLoc = new(ids.Next("store"));
        UserId user = new(ids.Next("user"));
        PeddlerId peddler = new(ids.Next("peddler"));

        var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        store.RegisterTrip(new Trip(trip, peddler, route, when, user));

        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "P-OPEN", trip, Array.Empty<StockLoadLine>(), user, when));
        store.Bind();
        Result<AppendReceipt> open = store.Append(new[]
        {
            Moves.OpeningStock(0, when, user, new LoadRef(openId), product, storeLoc, Quantity.Of(100)),
            Moves.OpeningStock(1, when, user, new LoadRef(openId), kopi, storeLoc, Quantity.Of(100)),
        });
        Assert.True(open.IsSuccess, open.Errors.FirstOrDefault()?.Code ?? "opening stock failed");

        var loadId = new LoadId(ids.Next("load"));
        Result<LoadDraft> load = LoadBuilder.Build(store.Context, new LoadCommand(
            loadId, "P-LOAD", trip, storeLoc,
            new[]
            {
                new LoadLine(product, SardinesSku, "Sardines", Quantity.Of(50), Money.FromPesos(30m)),
                new LoadLine(kopi, KopiSku, "Kopi", Quantity.Of(50), Money.FromPesos(30m)),
            },
            user, when));
        Assert.True(load.IsSuccess, load.Errors.FirstOrDefault()?.Code ?? "load build failed");
        store.RegisterLoad(load.Value.Document);
        store.Bind();
        Result<AppendReceipt> loadAppend = store.Append(load.Value.Movements);
        Assert.True(loadAppend.IsSuccess, loadAppend.Errors.FirstOrDefault()?.Code ?? "load append failed");

        services.GetRequiredService<IUserAccountStore>().AddAsync(new User
        {
            Id = user,
            Username = SellerUsername,
            PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(SellerPassword),
            Role = Role.Peddler,
            PeddlerLink = peddler,
            CreatedBy = user,
            CreatedUtc = when,
        }).GetAwaiter().GetResult();

        var products = services.GetRequiredService<IProductStore>();
        products.UpsertBySkuAsync(new Product(
            product, SardinesSku, Barcode, "Sardines", "canned", new Category("Canned"),
            "piece", Money.FromPesos(45m), null, null, false, null,
            SupportsPwdDiscount: true, SupportsSeniorDiscount: false))
            .GetAwaiter().GetResult();
        products.UpsertBySkuAsync(new Product(
            kopi, KopiSku, "4801234567891", "Kopi", "coffee", new Category("Drinks"),
            "sachet", Money.FromPesos(10m), null, null, false, null,
            SupportsPwdDiscount: false, SupportsSeniorDiscount: false))
            .GetAwaiter().GetResult();

        if (signIn)
        {
            services.GetRequiredService<IActorContext>().Set(user);
        }
    }

    private static void EnsureMigrated(ServiceProvider services) =>
        services.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();
}
