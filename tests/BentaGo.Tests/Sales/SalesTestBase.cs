using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

/// <summary>
/// Shared harness for the Sales bucket (BG-006, namespace BentaGo.Tests.Sales
/// so `--filter Sales` runs it): fresh SQLite roots, migrated hosts, a seeded
/// peddler-linked user, an open trip with stock, and the session-related
/// services. Same real-component discipline as the Persistence bucket.
/// </summary>
public abstract class SalesTestBase : PersistenceTestBase
{
    protected const string Sku = "SKU";
    protected const string Barcode = "0123456789012";

    protected static readonly Category DefaultCategory = new("Test Category");

    protected static Product TestProduct(ProductId id, string sku = Sku, string? barcode = Barcode,
        string name = "Test Product", Money? price = null, bool pwd = false, bool senior = false) =>
        new(id, sku, barcode, name, "description", DefaultCategory, "piece",
            price ?? Money.FromPesos(45m), null, null, false, null, pwd, senior);

    protected sealed class SalesHost : IDisposable
    {
        public SalesHost(ServiceProvider provider)
        {
            Provider = provider;
            Store = provider.GetRequiredService<SqliteLedgerStore>();
            Products = provider.GetRequiredService<IProductStore>();
            Checkout = provider.GetRequiredService<ICheckoutService>();
            Users = provider.GetRequiredService<IUserAccountStore>();
            Actors = provider.GetRequiredService<IActorContext>();
            Trips = provider.GetRequiredService<ITripReadService>();
        }

        public ServiceProvider Provider { get; }
        public SqliteLedgerStore Store { get; }
        public IProductStore Products { get; }
        public ICheckoutService Checkout { get; }
        public IUserAccountStore Users { get; }
        public IActorContext Actors { get; }
        public ITripReadService Trips { get; }

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>Migrated host + seeded peddler account (default active with a
    /// link) + an open trip with 50 units loaded to the route.</summary>
    protected async Task<(SalesHost Host, TripId Trip, PeddlerId Peddler, UserId User, ProductId Product, Product CatalogProduct)> SeededAsync(
        bool linked = true, bool deactivated = false, bool openTrip = true, bool stock = true)
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var host = new SalesHost(provider);

        (TripId trip, ProductId product, LocationId route, LocationId store, UserId user, PeddlerId peddler) = Known();

        var actor = new User
        {
            Id = user,
            Username = "seller",
            PasswordHash = "hash-not-checked-here",
            Role = Role.Peddler,
            PeddlerLink = linked ? peddler : null,
            DeactivatedUtc = deactivated ? SeedWhen : null,
            CreatedBy = user,
            CreatedUtc = SeedWhen,
        };
        await host.Users.AddAsync(actor);

        if (openTrip)
        {
            if (stock)
            {
                // SeedDay registers the trip, opens stock at the store, and
                // loads 50 units to the route.
                SeedDay(host.Store, trip, product, route, store, user, peddler);
            }
            else
            {
                host.Store.RegisterTrip(new Trip(trip, peddler, route, SeedWhen, user));
            }
        }

        Product catalog = TestProduct(product);
        await host.Products.UpsertBySkuAsync(catalog);

        return (host, trip, peddler, user, product, catalog);
    }

    protected CheckoutSession NewSession(SalesHost host, TripId trip, PeddlerId peddler, UserId user, ISaleNumberSource numbers)
    {
        host.Store.Bind();
        Trip? open = null;
        foreach (Trip t in host.Store.Context.Trips)
        {
            if (t.Id == trip)
            {
                open = t;
                break;
            }
        }

        Assert.NotNull(open);
        return new CheckoutSession(
            host.Store, host.Products, host.Checkout, numbers,
            host.Actors, new User { Id = user }, () => SeedWhen.AddHours(2), open!);
    }

    protected CheckoutSessionFactory NewFactory(SalesHost host) => new(
        host.Store, host.Products, host.Checkout,
        new FixedNumberSource(new Queue<string>(new[] { "R-T1", "R-T2", "R-T3", "R-T4", "R-T5" })),
        host.Actors, host.Users, () => SeedWhen.AddHours(2));
}
