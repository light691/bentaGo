using System.Diagnostics;
using System.IO;
using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Catalog;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales.Perf;

/// <summary>
/// BG-006 perf box (docs/05): product-lookup latency and checkout-completion
/// latency on the target data volume — 50,000 products and 100,000 movements.
/// Rules: P95 lookup &lt;= 100 ms, P95 checkout &lt;= 2 s. Baselines measured
/// FIRST on the same data: the BG-005 search surface (IProductStore
/// FindBySkuAsync) and the BG-003 transaction surface (ICheckoutService
/// Checkout, no session overhead). A warm-up run is excluded from both
/// samples (docs/05 probe discipline). The report is written to the path in
/// BENTAGO_PERF_REPORT when set (verification drain); the asserts always run.
/// </summary>
public sealed class SalesPerfTests : SalesTestBase
{
    private const int ProductCount = 50_000;
    private const int MovementCount = 100_000;
    private const int LookupRuns = 1_000;
    private const int CheckoutRuns = 100;
    private const int P95LookupLimitMs = 100;
    private const int P95CheckoutLimitMs = 2_000;

    private static string SkuFor(int index) => $"SKU-{index:D6}";

    /// <summary>
    /// Proves the perf-factor assumptions: a registered trip + load opens the
    /// trip when a transfer pair is appended (the docs must be bound first —
    /// Bind() after RegisterTrip), and the Persistence SeedDay shape agrees.
    /// </summary>
    [Fact]
    public async Task Diagnostic_TripOpensOnRegisteredLoadTransferPair()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        using var host = new SalesHost(provider, root);
        (TripId trip, ProductId product, LocationId route, LocationId storeLoc, UserId user, PeddlerId peddler) = Known();

        // KNOWN-GOOD: Persistence SeedDay (LoadBuilder-built load movements).
        SeedDay(host.Store, trip, product, route, storeLoc, user, peddler);
        host.Store.Bind();
        Assert.Equal(TripStatus.Open, host.Store.Context.State.Trip(trip).Status);

        // Hand-rolled pair over a registered empty load, with the docs bound:
        // opens the trip the same way.
        string root2 = NewRoot();
        ServiceProvider provider2 = NewHost(root2);
        await provider2.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        using var host2 = new SalesHost(provider2, root2);
        (TripId trip2, ProductId product2, LocationId route2, LocationId store2, UserId user2, PeddlerId peddler2) = Known();
        host2.Store.RegisterTrip(new Trip(trip2, peddler2, route2, SeedWhen, user2));
        host2.Store.Bind();
        var loadId = new LoadId(Guid.NewGuid());
        var load = new LoadRef(loadId);
        Result<AppendReceipt> first = host2.Store.Append(new[]
        {
            Moves.OpeningStock(0, SeedWhen, user2, load, product2, store2, Quantity.Of(100)),
        });
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code ?? "open failed");
        host2.Store.RegisterLoad(new StockLoad(loadId, "P-DIAG", trip2, Array.Empty<StockLoadLine>(), user2, SeedWhen));
        Result<AppendReceipt> second = host2.Store.Append(new Movement[]
        {
            Moves.TransferOut(0, SeedWhen, user2, load, product2, store2, Quantity.Of(100)),
            Moves.TransferIn(0, SeedWhen, user2, load, product2, route2, Quantity.Of(100)),
        });
        Assert.True(second.IsSuccess, second.Errors.FirstOrDefault()?.Code ?? "transfer failed");
        host2.Store.Bind();
        Assert.Equal(TripStatus.Open, host2.Store.Context.State.Trip(trip2).Status);
    }

    [Fact]
    public async Task P95_LookupAndCheckout_On50kProductsAnd100kMovements()
    {
        string root = NewRoot();
        ServiceProvider provider = NewHost(root);
        await provider.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();
        using var host = new SalesHost(provider, root);

        (TripId trip, ProductId _, LocationId route, LocationId storeLoc, UserId user, PeddlerId peddler) = Known();

        await host.Users.AddAsync(new User
        {
            Id = user,
            Username = "perf-seller",
            PasswordHash = "hash-not-checked-here",
            Role = Role.Peddler,
            PeddlerLink = peddler,
            CreatedBy = user,
            CreatedUtc = SeedWhen,
        });
        host.Store.RegisterTrip(new Trip(trip, peddler, route, SeedWhen, user));
        host.Store.Bind();
        host.Actors.Set(user);

        var seedSink = new List<(string Label, long Ms)>();
        ProductId[] pids = Enumerable.Range(0, ProductCount)
            .Select(_ => new ProductId(Guid.NewGuid())).ToArray();
        var seedWatch = Stopwatch.StartNew();
        await BulkCatalogAsync(provider, pids);
        seedSink.Add(("seed catalog", seedWatch.ElapsedMilliseconds));
        seedWatch.Restart();
        SeedMovements(host, trip, route, storeLoc, user, pids);
        seedSink.Add(("seed movements", seedWatch.ElapsedMilliseconds));
        host.Store.Bind();
        Assert.True(host.Store.Context.State.Trip(trip).Status == TripStatus.Open,
            $"trip did not open; status = {host.Store.Context.State.Trip(trip).Status} (movements appended: {host.Store.Context.State.LastSequence})");
        Assert.True(host.Store.Context.State.StockOnHand(new ProductId(Array.Empty<byte>().Length == 0 ? Guid.Empty : Guid.Empty), route).Units == 0);

        // --- Baselines first (docs/05: record the BG-005 / BG-003 surfaces
        // --- before the BG-006 probe) -----------------------------------------
        var baselineLookups = new List<long>(LookupRuns);
        var store = provider.GetRequiredService<IProductStore>();
        var baselineCheckouts = new List<long>(CheckoutRuns);
        var checkout = provider.GetRequiredService<ICheckoutService>();
        for (int i = 0; i < CheckoutRuns; i++)
        {
            // One warm-up, excluded from the sample.
            RunDirectCheckout(checkout, trip, user, pids, i, baselineCheckouts, warmUp: i == 0);
        }

        var probeLookups = new List<long>(LookupRuns);
        var probeCheckouts = new List<long>(CheckoutRuns);
        var search = provider.GetRequiredService<IProductSearchService>();
        var factory = provider.GetRequiredService<ICheckoutSessionFactory>();

        for (int i = 0; i < LookupRuns; i++)
        {
            string sku = SkuFor(i % ProductCount);
            var sw = Stopwatch.StartNew();
            Result<IReadOnlyList<ProductHit>> hit = await search.SearchAsync(sku);
            sw.Stop();
            Assert.True(hit.IsSuccess, hit.Errors.FirstOrDefault()?.Code ?? "search failed");
            if (i == 0)
            {
                Assert.NotEmpty(hit.Value); // warm-up doubles as a smoke check
            }

            if (i > 0)
            {
                probeLookups.Add(sw.ElapsedMilliseconds);
                baselineLookups.Add(await BaselineLookupMsAsync(store, sku));
            }
        }

        for (int i = 0; i < CheckoutRuns; i++)
        {
            await RunSessionCheckoutAsync(factory, store, i, probeCheckouts, warmUp: i == 0);
        }

        long lookupP95 = P95(probeLookups);
        long checkoutP95 = P95(probeCheckouts);
        long baseSearchP95 = P95(baselineLookups);
        long baseTxnP95 = P95(baselineCheckouts);

        string report = Report(seedSink, lookupP95, Median(probeLookups), checkoutP95, Median(probeCheckouts),
            baseSearchP95, baseTxnP95);
        string? reportPath = Environment.GetEnvironmentVariable("BENTAGO_PERF_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, report);
        }

        Assert.True(lookupP95 <= P95LookupLimitMs,
            $"P95 product lookup {lookupP95} ms exceeds {P95LookupLimitMs} ms (baseline search P95 {baseSearchP95} ms)");
        Assert.True(checkoutP95 <= P95CheckoutLimitMs,
            $"P95 checkout {checkoutP95} ms exceeds {P95CheckoutLimitMs} ms (baseline txn P95 {baseTxnP95} ms)");
    }

    private static async Task BulkCatalogAsync(ServiceProvider provider, IReadOnlyList<ProductId> pids)
    {
        var factory = provider.GetRequiredService<IDbContextFactory<BentaGoDbContext>>();
        for (int chunkStart = 0; chunkStart < ProductCount; chunkStart += 1_000)
        {
            await using var context = await factory.CreateDbContextAsync();
            var chunk = new List<Product>(1_000);
            for (int i = chunkStart; i < chunkStart + 1_000; i++)
            {
                chunk.Add(new Product(
                    pids[i], SkuFor(i), $"48{i:D11}", $"Product {i}",
                    "perf", new Category("Perf"), "piece", Money.FromPesos(45m), null, null,
                    false, null, SupportsPwdDiscount: true, SupportsSeniorDiscount: true));
            }

            context.Products.AddRange(chunk);
            await context.SaveChangesAsync();
        }
    }

    private static void SeedMovements(SalesHost host, TripId trip, LocationId route, LocationId store, UserId user,
        IReadOnlyList<ProductId> pids)
    {
        // Exactly 100,000 movements, all legal under the fold guards:
        //  - product 0 opens at the STORE (100) and one load pair moves its
        //    100 units onto the route — the first trip-attributed activity
        //    (opens the trip; see Diagnostic_TripOpensOnRegisteredLoadTransferPair)
        //  - products 1..N-1 open at the ROUTE (100 units each)
        //  - balance-neutral counts make the total exactly 100,000
        // Every sellable product has 100 units on the route (no STOCK_NEGATIVE).
        var countRef = new CountRef(new CountId(Guid.NewGuid()));

        // Opening batches: product 0 at the store, 1..N-1 at the route.
        var openings = new List<Movement>(ProductCount);
        openings.Add(Moves.OpeningStock(0, SeedWhen, user, new LoadRef(new LoadId(Guid.NewGuid())),
            pids[0], store, Quantity.Of(100)));
        for (int i = 1; i < ProductCount; i++)
        {
            openings.Add(Moves.OpeningStock(0, SeedWhen, user, new LoadRef(new LoadId(Guid.NewGuid())),
                pids[i], route, Quantity.Of(100)));
        }

        for (int start = 0; start < openings.Count; start += 1_000)
        {
            Result<AppendReceipt> appended = host.Store.Append(
                openings.GetRange(start, Math.Min(1_000, openings.Count - start)));
            Assert.True(appended.IsSuccess,
                $"opening batch at {start}: {appended.Errors.FirstOrDefault()?.Code ?? "append failed"}");
        }

        // The one load pair (registered right before the append, so the batch
        // binds it; the append clears staged docs after commit).
        var loadId = new LoadId(Guid.NewGuid());
        var load = new LoadRef(loadId);
        host.Store.RegisterLoad(new StockLoad(loadId, "P-PERF", trip, Array.Empty<StockLoadLine>(), user, SeedWhen));
        Result<AppendReceipt> pair = host.Store.Append(new Movement[]
        {
            Moves.TransferOut(0, SeedWhen, user, load, pids[0], store, Quantity.Of(100)),
            Moves.TransferIn(0, SeedWhen, user, load, pids[0], route, Quantity.Of(100)),
        });
        Assert.True(pair.IsSuccess, pair.Errors.FirstOrDefault()?.Code ?? "load pair failed");

        // Balance-neutral counts: 49,998 rows.
        for (int start = 0; start < ProductCount - 2; start += 1_000)
        {
            var batch = new List<Movement>(1_000);
            for (int i = start; i < start + 1_000 && i < ProductCount - 2; i++)
            {
                batch.Add(Moves.CountObserved(0, SeedWhen, user, countRef, pids[i], route, Quantity.Of(100)));
            }

            Result<AppendReceipt> appended = host.Store.Append(batch);
            Assert.True(appended.IsSuccess,
                $"count batch at {start}: {appended.Errors.FirstOrDefault()?.Code ?? "append failed"}");
        }
    }

    private void RunDirectCheckout(ICheckoutService checkout, TripId trip, UserId user, IReadOnlyList<ProductId> pids,
        int i, List<long> sample, bool warmUp)
    {
        ProductId product = pids[i];
        var lineId = new SaleLineId(Guid.NewGuid());
        var paymentId = new PaymentLineId(Guid.NewGuid());
        var cmd = new NewSaleCommand(
            new SaleId(Guid.NewGuid()), trip, $"R-PERF-C-{i:D4}", user, SeedWhen.AddHours(2),
            new[]
            {
                new NewSaleLine(lineId, product, SkuFor(i), $"Product {i}",
                    Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
            },
            new[]
            {
                new NewPaymentLine(paymentId, PaymentMethod.Cash, "",
                    Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero),
            });

        var sw = Stopwatch.StartNew();
        Result<CheckoutOutcome> outcome = checkout.Checkout(new CheckoutKey(Guid.NewGuid()), cmd);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.Errors.FirstOrDefault()?.Code ?? "checkout failed");
        if (!warmUp)
        {
            sample.Add(sw.ElapsedMilliseconds);
        }
    }

    private async Task RunSessionCheckoutAsync(ICheckoutSessionFactory factory, IProductStore store,
        int i, List<long> sample, bool warmUp)
    {
        Result<ICheckoutSession> created = await factory.CreateSessionAsync();
        Assert.True(created.IsSuccess, created.Errors.FirstOrDefault()?.Code ?? "create failed");
        ICheckoutSession session = created.Value!;
        Product? product = await store.FindBySkuAsync(SkuFor(i));
        Assert.NotNull(product);

        Assert.True(session.AddProduct(product!, Quantity.Of(1)).IsSuccess);
        Assert.True(session.SetPayment(
            new[] { new PaymentLineDraft(PaymentMethod.Cash, Money.FromPesos(45m), null) },
            Money.Zero).IsSuccess);

        var sw = Stopwatch.StartNew();
        Result<SavedSaleState> submitted = await session.SubmitAsync();
        sw.Stop();
        Assert.True(submitted.IsSuccess, submitted.Errors.FirstOrDefault()?.Code ?? "submit failed");
        if (!warmUp)
        {
            sample.Add(sw.ElapsedMilliseconds);
        }
    }

    private static async Task<long> BaselineLookupMsAsync(IProductStore store, string sku)
    {
        var sw = Stopwatch.StartNew();
        Product? product = await store.FindBySkuAsync(sku);
        sw.Stop();
        Assert.NotNull(product);
        return sw.ElapsedMilliseconds;
    }

    private static long P95(IReadOnlyList<long> samples)
    {
        long[] sorted = samples.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1);
        return sorted[index];
    }

    private static long Median(IReadOnlyList<long> samples)
    {
        long[] sorted = samples.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static string Report(
        IReadOnlyList<(string Label, long Ms)> seed,
        long lookupP95, long lookupMedian,
        long checkoutP95, long checkoutMedian,
        long baselineSearchP95, long baselineTxnP95)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# BG-006 perf probe — {ProductCount} products, {MovementCount} movements, {LookupRuns} lookups, {CheckoutRuns} checkouts (1 warm-up excluded)");
        foreach ((string label, long ms) in seed)
        {
            sb.AppendLine($"seed\t{label}\t{ms} ms");
        }

        sb.AppendLine($"metric\tbg-006 lookup P95\t{lookupP95} ms\trule <= {P95LookupLimitMs} ms");
        sb.AppendLine($"metric\tbg-006 lookup median\t{lookupMedian} ms");
        sb.AppendLine($"metric\tbg-006 session checkout P95\t{checkoutP95} ms\trule <= {P95CheckoutLimitMs} ms");
        sb.AppendLine($"metric\tbg-006 session checkout median\t{checkoutMedian} ms");
        sb.AppendLine($"baseline\tbg-005 search (ProductStore.FindBySkuAsync) P95\t{baselineSearchP95} ms");
        sb.AppendLine($"baseline\tbg-003 txn (ICheckoutService.Checkout) P95\t{baselineTxnP95} ms");
        return sb.ToString();
    }
}
