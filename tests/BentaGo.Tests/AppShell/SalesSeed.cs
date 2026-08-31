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
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// BG-006b shared seed for the AppShell tests that drive the REAL
/// session-backed sales flow (DialogResultTests, SalesUxTests, STA probes):
/// a migrated root, today's open trading day with 100 units of each product
/// on hand at the warehouse, a signed-in supervisor, and the catalog products
/// the tests scan.
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

        ProductId product = new(ids.Next("product"));
        ProductId kopi = new(ids.Next("kopi"));
        UserId user = new(ids.Next("user"));

        // The store keys the day on the host clock (today's Manila day), so
        // the seed opens TODAY, not a fixed date.
        var when = DateTimeOffset.UtcNow;
        DayKey day = DayKey.FromUtc(when);

        store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), day, 0, user, when));
        store.Bind();

        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(store.Context, new GoodsReceiptCommand(
            new GoodsId(ids.Next("goods")), day,
            new[]
            {
                new GoodsReceiptLineCommand(product, SardinesSku, "Sardines", Quantity.Of(100), Money.FromPesos(30m)),
                new GoodsReceiptLineCommand(kopi, KopiSku, "Kopi", Quantity.Of(100), Money.FromPesos(30m)),
            },
            user, when));
        Assert.True(goods.IsSuccess, goods.Errors.FirstOrDefault()?.Code ?? "goods receipt build failed");
        store.RegisterGoodsReceipt(goods.Value.Document);
        Result<AppendReceipt> goodsAppend = store.Append(goods.Value.Movements);
        Assert.True(goodsAppend.IsSuccess, goodsAppend.Errors.FirstOrDefault()?.Code ?? "goods receipt append failed");

        services.GetRequiredService<IUserAccountStore>().AddAsync(new User
        {
            Id = user,
            Username = SellerUsername,
            PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(SellerPassword),
            Role = Role.Supervisor,
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
