using System.IO;
using System.Text;
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

    /// <summary>Known deterministic ids for a sellable day seed.</summary>
    protected static (ProductId Product, UserId User) Known()
    {
        var ids = new ScenarioIdSource("persist-known-v1");
        return (
            new ProductId(ids.Next("product")),
            new UserId(ids.Next("user")));
    }

    /// <summary>Opens the trading day and receives the opening stock on it.</summary>
    protected static void SeedDay(
        SqliteLedgerStore store,
        ProductId product,
        UserId user,
        DayKey? day = null,
        int opening = 100)
    {
        var ids = new ScenarioIdSource("persist-seed-v1");
        DayKey dayKey = day ?? SeedDayKey;

        store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), dayKey, 0, user, SeedWhen));
        store.Bind();

        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(store.Context, new GoodsReceiptCommand(
            new GoodsId(ids.Next("goods")), dayKey,
            new[] { new GoodsReceiptLineCommand(product, "SKU", "Product", Quantity.Of(opening), Money.FromPesos(30m)) },
            user, SeedWhen));
        Assert.True(goods.IsSuccess, goods.Errors.FirstOrDefault()?.Code ?? "goods receipt build failed");
        Result<AppendReceipt> append = store.Append(goods.Value.Movements);
        Assert.True(append.IsSuccess, append.Errors.FirstOrDefault()?.Code ?? "goods receipt append failed");
    }

    protected static DateTimeOffset SeedWhen { get; } = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    protected static DayKey SeedDayKey { get; } = DayKey.FromUtc(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));

    protected static NewSaleCommand OneUnitSale(DayKey day, ProductId product, string receipt, UserId user, int qty = 1)
    {
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        return new NewSaleCommand(
            new SaleId(Guid.NewGuid()), day, receipt, user, SeedWhen.AddHours(1),
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

/// <summary>
/// Deterministic single-store day scenario (the BG-P1a replacement for the
/// deleted two-peddler trip scenario): one trading day with a float, goods
/// receipts, cash/GCash sales, a void, a return with refund, physical counts,
/// and a day close. Same seed -> same ids -> byte-identical renders.
/// </summary>
public static class SingleDayScenario
{
    public static readonly DateTimeOffset When = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    public static DayKey Day { get; } = DayKey.FromUtc(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));

    public static ScenarioReport Run(ILedgerStore store)
    {
        var ids = new ScenarioIdSource("single-day-v1");
        var user = new UserId(ids.Next("user"));
        var approver = new UserId(ids.Next("approver"));
        var sardines = new ProductId(ids.Next("sardines"));
        var kopi = new ProductId(ids.Next("kopi"));

        // Open the day with a P100 float (a document — posts no movement).
        store.RegisterDayOpen(new DayOpen(new DayId(ids.Next("day")), Day, 10_000, user, When));
        store.Bind();

        // Goods receipts: 100 sardines, 50 kopi, at the warehouse.
        Result<GoodsReceiptDraft> goods = GoodsReceiptBuilder.Build(store.Context, new GoodsReceiptCommand(
            new GoodsId(ids.Next("goods")), Day,
            new[]
            {
                new GoodsReceiptLineCommand(sardines, "Sardines", "Sardines", Quantity.Of(100), Money.FromPesos(30m)),
                new GoodsReceiptLineCommand(kopi, "Kopi", "Kopi", Quantity.Of(50), Money.FromPesos(50m)),
            },
            user, When));
        Assert.True(goods.IsSuccess, goods.Errors.FirstOrDefault()?.Code ?? "goods build failed");
        Assert.True(store.Append(goods.Value.Movements).IsSuccess, "goods append failed");

        // R-000001: 5 sardines cash (225.00).
        CommitSale(store, ids, user, "R-000001",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l1")), sardines, "Sardines", "Sardines", Money.FromPesos(45m), Quantity.Of(5), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p1")), PaymentMethod.Cash, "", Money.FromPesos(225m), Money.FromPesos(225m), Money.Zero) });

        // R-000002: 2 kopi GCash (100.00).
        CommitSale(store, ids, user, "R-000002",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l2")), kopi, "Kopi", "Kopi", Money.FromPesos(50m), Quantity.Of(2), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p2")), PaymentMethod.Gcash, "G-SCEN", Money.FromPesos(100m), Money.Zero, Money.Zero) });

        // R-000003: 1 sardines cash (45.00), then voided (net zero).
        Sale sale3 = CommitSale(store, ids, user, "R-000003",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l3")), sardines, "Sardines", "Sardines", Money.FromPesos(45m), Quantity.Of(1), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p3")), PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero) });
        Result<VoidDraft> voidDraft = VoidBuilder.Build(store.Context, new VoidCommand(
            new VoidId(ids.Next("void")), sale3.Id, "customer changed mind", user, When.AddHours(2), ApprovedBy: approver));
        Assert.True(voidDraft.IsSuccess, voidDraft.Errors.FirstOrDefault()?.Code ?? "void build failed");
        store.RegisterVoid(voidDraft.Value.Document);
        store.Bind();
        Assert.True(store.Append(voidDraft.Value.Movements).IsSuccess, "void append failed");

        // R-000004: 2 sardines cash (90.00), then 1 returned with a cash refund.
        Sale sale4 = CommitSale(store, ids, user, "R-000004",
            new[] { new NewSaleLine(new SaleLineId(ids.Next("l4")), sardines, "Sardines", "Sardines", Money.FromPesos(45m), Quantity.Of(2), Money.Zero) },
            new[] { new NewPaymentLine(new PaymentLineId(ids.Next("p4")), PaymentMethod.Cash, "", Money.FromPesos(90m), Money.FromPesos(90m), Money.Zero) });
        Result<ReturnDraft> returnDraft = ReturnBuilder.Build(store.Context, new ReturnCommand(
            new ReturnId(ids.Next("return")), sale4.Id,
            new[] { new ReturnLineCommand(sale4.Lines[0].Id, Quantity.Of(1), "damaged can", PaymentMethod.Cash, Money.FromPesos(45m)) },
            user, When.AddHours(3)));
        Assert.True(returnDraft.IsSuccess, returnDraft.Errors.FirstOrDefault()?.Code ?? "return build failed");
        store.RegisterReturn(returnDraft.Value.Document);
        store.Bind();
        Assert.True(store.Append(returnDraft.Value.Movements).IsSuccess, "return append failed");

        // Physical counts: sardines 96 (one over), kopi 48 (exact).
        Result<CountDraft> count = CountBuilder.Build(store.Context, new CountCommand(
            new CountId(ids.Next("count")), StoreLocations.Warehouse,
            new[]
            {
                new CountLineCommand(sardines, Quantity.Of(96), "one can returned unopened"),
                new CountLineCommand(kopi, Quantity.Of(48), null),
            },
            user, When.AddHours(4)));
        Assert.True(count.IsSuccess, count.Errors.FirstOrDefault()?.Code ?? "count build failed");
        store.RegisterCount(count.Value.Document);
        store.Bind();
        Assert.True(store.Append(count.Value.Movements).IsSuccess, "count append failed");

        // Day close: physical cash folded to 270.00; the operator counted
        // 180.00 -> the report shorts by 90.00 (the close doc's own expected
        // is the day float per the P1a slice, so its label is Overage).
        Result<DayCloseDraft> close = DayBuilder.Close(store.Context, new DayCloseCommand(
            new CloseId(ids.Next("close")), Day, CountedCashCentavos: 18_000, "single day close", user, When.AddHours(5)));
        Assert.True(close.IsSuccess, close.Errors.FirstOrDefault()?.Code ?? "close build failed");
        store.RegisterDayClose(close.Value.Document);
        store.Bind();

        // The day ends REOPENED so lanes can keep driving counts against it.
        Result<DayReopenDraft> reopen = DayBuilder.Reopen(store.Context, new DayReopenCommand(
            new DayId(ids.Next("reopen")), Day, "recount needed", approver, user, When.AddHours(6)));
        Assert.True(reopen.IsSuccess, reopen.Errors.FirstOrDefault()?.Code ?? "reopen build failed");
        store.RegisterDayReopen(reopen.Value.Document);
        store.Bind();

        return Render(store);
    }

    public static ScenarioReport Render(ILedgerStore store)
    {
        store.Bind();
        var sb = new StringBuilder();
        sb.AppendLine($"DAY {Day} {store.State.Day(Day)}");
        foreach (Fund fund in new[] { Fund.PhysicalCash, Fund.Gcash, Fund.Maya, Fund.Bank })
        {
            sb.AppendLine($"FUND {fund} {store.State.FundsOnHand(fund).ToPesos():0.00}");
        }

        var cells = new HashSet<(ProductId Product, LocationId Location)>();
        foreach (Movement movement in store.Movements)
        {
            (ProductId, LocationId)? cell = movement switch
            {
                GoodsReceived m => (m.ProductId, m.LocationId),
                SaleIssue m => (m.ProductId, m.LocationId),
                SaleIssueRevoke m => (m.ProductId, m.LocationId),
                ReturnToStock m => (m.ProductId, m.LocationId),
                Damage m => (m.ProductId, m.LocationId),
                Loss m => (m.ProductId, m.LocationId),
                AdjustmentIncrease m => (m.ProductId, m.LocationId),
                AdjustmentDecrease m => (m.ProductId, m.LocationId),
                CountObserved m => (m.ProductId, m.LocationId),
                _ => null,
            };
            if (cell is { } c)
            {
                cells.Add(c);
            }
        }

        foreach ((ProductId product, LocationId location) in cells.OrderBy(c => c.Product.Value))
        {
            sb.AppendLine($"STOCK {product.Value} {store.State.StockOnHand(product, location).Units}");
            sb.AppendLine($"OBSERVED {product.Value} {store.State.ObservedUnits(product, location).Units}");
        }

        sb.AppendLine(
            $"DOCS sales={store.Context.Sales.Count} returns={store.Context.Returns.Count} " +
            $"voids={store.Context.Voids.Count} counts={store.Context.Counts.Count} " +
            $"closes={store.Context.DayCloses.Count} reopens={store.Context.DayReopens.Count}");

        DayClose? close = store.Context.DayCloses
            .OrderBy(c => c.WhenUtc).ThenBy(c => c.Id.Value)
            .LastOrDefault();
        if (close is { } theClose)
        {
            sb.AppendLine(
                $"CLOSE {theClose.ExpectedCashCentavos} {theClose.CountedCashCentavos} " +
                $"{theClose.DifferenceCentavos} {theClose.Label} {theClose.Notes}");
        }

        return new ScenarioReport(sb.ToString());
    }

    private static Sale CommitSale(
        ILedgerStore store, ScenarioIdSource ids, UserId user, string receipt,
        NewSaleLine[] lines, NewPaymentLine[] payments)
    {
        Result<SaleDraft> draft = SaleBuilder.Build(store.Context, new NewSaleCommand(
            new SaleId(ids.Next($"sale-{receipt}")), Day, receipt, user, When.AddHours(1), lines, payments));
        Assert.True(draft.IsSuccess, draft.Errors.FirstOrDefault()?.Code ?? $"{receipt} build failed");
        store.RegisterSale(draft.Value.Document);
        store.Bind();
        Assert.True(store.Append(draft.Value.Movements).IsSuccess, $"{receipt} append failed");
        return draft.Value.Document;
    }
}
