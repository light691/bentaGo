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
/// supervisor account, an open trading day with stock, and the session-related
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
        public SalesHost(ServiceProvider provider, string root)
        {
            Provider = provider;
            Root = root;
            Store = provider.GetRequiredService<SqliteLedgerStore>();
            Products = provider.GetRequiredService<IProductStore>();
            Checkout = provider.GetRequiredService<ICheckoutService>();
            Users = provider.GetRequiredService<IUserAccountStore>();
            Actors = provider.GetRequiredService<IActorContext>();
        }

        public ServiceProvider Provider { get; }
        public string Root { get; }
        public SqliteLedgerStore Store { get; }
        public IProductStore Products { get; }
        public ICheckoutService Checkout { get; }
        public IUserAccountStore Users { get; }
        public IActorContext Actors { get; }

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>Migrated host + seeded supervisor account (default active) +
    /// an open trading day with 100 units on hand at the warehouse.</summary>
    protected async Task<(SalesHost Host, DayKey Day, UserId User, ProductId Product, Product CatalogProduct)> SeededAsync(
        bool deactivated = false, bool openDay = true, bool stock = true)
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        var host = new SalesHost(provider, root);

        (ProductId product, UserId user) = Known();

        var actor = new User
        {
            Id = user,
            Username = "seller",
            PasswordHash = "hash-not-checked-here",
            Role = Role.Supervisor,
            DeactivatedUtc = deactivated ? SeedWhen : null,
            CreatedBy = user,
            CreatedUtc = SeedWhen,
        };
        await host.Users.AddAsync(actor);

        if (openDay)
        {
            if (stock)
            {
                SeedDay(host.Store, product, user);
            }
            else
            {
                host.Store.RegisterDayOpen(new DayOpen(new DayId(Guid.NewGuid()), SeedDayKey, 0, user, SeedWhen));
                host.Store.Bind();
            }
        }

        Product catalog = TestProduct(product);
        await host.Products.UpsertBySkuAsync(catalog);

        return (host, SeedDayKey, user, product, catalog);
    }

    protected CheckoutSession NewSession(SalesHost host, UserId user, ISaleNumberSource numbers)
    {
        host.Store.Bind();
        return new CheckoutSession(
            host.Store, host.Products, host.Checkout, numbers,
            host.Actors, new User { Id = user }, () => SeedWhen.AddHours(2));
    }

    protected CheckoutSessionFactory NewFactory(SalesHost host) => new(
        host.Store, host.Products, host.Checkout,
        new FixedNumberSource(new Queue<string>(new[] { "R-T1", "R-T2", "R-T3", "R-T4", "R-T5" })),
        host.Actors, host.Users, () => SeedWhen.AddHours(2));
}
