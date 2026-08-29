using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// BG-005c persisted trip-lifecycle lanes: cancellation is durable (TripStates
/// 'Cancelled', survives reopen + rebuild, locks a SECOND store over the same
/// file), peddler isolation holds across stores (one active trip per peddler),
/// and the negative-stock reject on the live single-movement API is a
/// DomainError (STOCK_NEGATIVE) — the v1 contract's "row error" framing is
/// the batch/import context (docs/05 v1 + BG-005b stock_qty decision), NOT
/// the live API, and that split is asserted here.
/// </summary>
public sealed class TripCancelPersistenceTests : PersistenceTestBase
{
    [Fact]
    public async Task Cancel_IsPersisted_SurvivesReopenAndRebuild_AndLocksCrossStore()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var ids = new ScenarioIdSource("persist-cancel-v1");
        var admin = new UserId(ids.Next("admin"));
        var peddler = new PeddlerId(ids.Next("peddler"));
        var storeLoc = new LocationId(ids.Next("store"));
        var route = new LocationId(ids.Next("route"));
        var product = new ProductId(ids.Next("product"));
        var trip = new TripId(ids.Next("trip"));

        Result<Trip> opened = host.GetRequiredService<IOpenTripService>().OpenTrip(
            new NewTripCommand(trip, peddler, route, admin, SeedWhen));
        Assert.True(opened.IsSuccess, opened.Errors.FirstOrDefault()?.Code);

        var store = host.GetRequiredService<SqliteLedgerStore>();
        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "P-CANCEL-OPEN", default, Array.Empty<StockLoadLine>(), admin, SeedWhen));
        store.Bind();
        Result<AppendReceipt> opening = store.Append(new[]
        {
            Moves.OpeningStock(0, SeedWhen, admin, new LoadRef(openId), product, storeLoc, Quantity.Of(100)),
        });
        Assert.True(opening.IsSuccess, opening.Errors.FirstOrDefault()?.Code);

        Result<LoadOutcome> loaded = host.GetRequiredService<ILoadService>().Load(new LoadCommand(
            new LoadId(ids.Next("load")), "P-CANCEL-LD", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(30), Money.FromPesos(30m)) },
            admin, SeedWhen.AddHours(1)), peddler);
        Assert.True(loaded.IsSuccess, loaded.Errors.FirstOrDefault()?.Code);

        Result<CancelOutcome> cancelled = host.GetRequiredService<ICancelTripService>().Cancel(
            new CancelTripCommand(trip, "route cancelled by admin", admin, SeedWhen.AddHours(2)), peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        // The revert is durable: route zeroed, store restored, trip Cancelled.
        Assert.Equal(0, store.State.StockOnHand(product, route).Units);
        Assert.Equal(100, store.State.StockOnHand(product, storeLoc).Units);
        Assert.Equal(TripStatus.Cancelled, store.State.Trip(trip).Status);
        Assert.Equal("Cancelled", await ReadTripStatusAsync(DbPath(root), trip));

        // Transitions are audited with EXISTING categories (documented reuse:
        // REOPEN for the open transition, CLOSE for the cancel — no TRIP.* set).
        Assert.Contains(store.Audits, a => a.Category == "REOPEN" && a.Message.Contains($"trip {trip} opened"));
        Assert.Contains(store.Audits, a => a.Category == "CLOSE" && a.Message.Contains("cancelled: route cancelled by admin"));

        // Reopen + rebuild: the fold replays TripCancel and keeps Cancelled.
        store.Close();
        var reopened = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        Assert.Equal(TripStatus.Cancelled, reopened.State.Trip(trip).Status);
        await reopened.RebuildAsync();
        Assert.Equal(TripStatus.Cancelled, reopened.State.Trip(trip).Status);
        Assert.Equal("Cancelled", await ReadTripStatusAsync(DbPath(root), trip));
        Assert.Equal("ok", await SqliteTest.QuickCheckAsync(DbPath(root)));

        // A second store over the same file is locked: no sale on the trip.
        Result<CheckoutOutcome> blocked = host.GetRequiredService<ICheckoutService>().Checkout(
            new CheckoutKey(Guid.NewGuid()),
            OneUnitSale(trip, product, "R-CANCEL-REJECT", admin));
        Assert.True(blocked.IsFailure);
        Assert.Equal("TRIP_CANCELLED", blocked.Errors[0].Code);
        Assert.Equal(0, await SqliteTest.CountAsync(DbPath(root), "Sales"));
    }

    [Fact]
    public async Task OneActiveTrip_EnforcedAcrossStores()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var ids = new ScenarioIdSource("persist-one-active-v1");
        var admin = new UserId(ids.Next("admin"));
        var peddler = new PeddlerId(ids.Next("peddler"));
        var route = new LocationId(ids.Next("route"));
        var tripA = new TripId(ids.Next("trip-a"));
        var tripB = new TripId(ids.Next("trip-b"));

        var store1 = host.GetRequiredService<SqliteLedgerStore>();
        Result<Trip> first = host.GetRequiredService<IOpenTripService>().OpenTrip(
            new NewTripCommand(tripA, peddler, route, admin, SeedWhen));
        Assert.True(first.IsSuccess, first.Errors.FirstOrDefault()?.Code);

        // A second store over the same file must also see the active trip
        // (the service Binds committed trips before the check).
        var store2 = new SqliteLedgerStore(new DataPathProvider(root), new DebugOptions());
        var openFromStore2 = new BentaGo.Application.Services.OpenTripService(store2);
        Result<Trip> duplicate = openFromStore2.OpenTrip(
            new NewTripCommand(tripB, peddler, route, admin, SeedWhen.AddMinutes(5)));
        Assert.True(duplicate.IsFailure);
        Assert.Equal("TRIP_ALREADY_ACTIVE", duplicate.Errors[0].Code);

        // Terminal via cancel frees the slot: a new trip opens from store2.
        var cancelFromStore2 = new BentaGo.Application.Services.CancelTripService(store2);
        Result<CancelOutcome> cancelled = cancelFromStore2.Cancel(
            new CancelTripCommand(tripA, "no longer needed", admin, SeedWhen.AddHours(1)), peddler);
        Assert.True(cancelled.IsSuccess, cancelled.Errors.FirstOrDefault()?.Code);

        var tripC = new TripId(ids.Next("trip-c"));
        Result<Trip> after = openFromStore2.OpenTrip(
            new NewTripCommand(tripC, peddler, route, admin, SeedWhen.AddHours(2)));
        Assert.True(after.IsSuccess, after.Errors.FirstOrDefault()?.Code);
    }

    [Fact]
    public async Task NegativeStockLoad_Overdraw_IsDomainError_OnLiveApi()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var ids = new ScenarioIdSource("persist-neg-stock-v1");
        var admin = new UserId(ids.Next("admin"));
        var peddler = new PeddlerId(ids.Next("peddler"));
        var storeLoc = new LocationId(ids.Next("store"));
        var route = new LocationId(ids.Next("route"));
        var product = new ProductId(ids.Next("product"));
        var trip = new TripId(ids.Next("trip"));

        Result<Trip> opened = host.GetRequiredService<IOpenTripService>().OpenTrip(
            new NewTripCommand(trip, peddler, route, admin, SeedWhen));
        Assert.True(opened.IsSuccess, opened.Errors.FirstOrDefault()?.Code);

        var store = host.GetRequiredService<SqliteLedgerStore>();
        var openId = new LoadId(ids.Next("open"));
        store.RegisterLoad(new StockLoad(openId, "P-NEG-OPEN", default, Array.Empty<StockLoadLine>(), admin, SeedWhen));
        store.Bind();
        Result<AppendReceipt> opening = store.Append(new[]
        {
            Moves.OpeningStock(0, SeedWhen, admin, new LoadRef(openId), product, storeLoc, Quantity.Of(100)),
        });
        Assert.True(opening.IsSuccess, opening.Errors.FirstOrDefault()?.Code);

        // Overdraw by 150 against 100: the live single-movement API rejects
        // with a structured DomainError (never silent, nothing committed).
        Result<LoadOutcome> overdraw = host.GetRequiredService<ILoadService>().Load(new LoadCommand(
            new LoadId(ids.Next("over")), "P-NEG-LD", trip, storeLoc,
            new[] { new LoadLine(product, "SKU", "Product", Quantity.Of(150), Money.FromPesos(30m)) },
            admin, SeedWhen.AddHours(1)), peddler);
        Assert.True(overdraw.IsFailure);
        Assert.Equal("STOCK_NEGATIVE", overdraw.Errors[0].Code);

        // Nothing committed: only the opening load + its movement exist, and
        // the store's fold is untouched (stock still 100 at the store).
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "StockLoads"));
        Assert.Equal(1, await SqliteTest.CountAsync(DbPath(root), "Movements"));
        Assert.Equal(100, store.State.StockOnHand(product, storeLoc).Units);
        Assert.Equal(0, store.State.StockOnHand(product, route).Units);
    }

    private static async Task<string?> ReadTripStatusAsync(string dbPath, TripId trip)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"status\" FROM \"TripStates\" WHERE \"trip_id\" = $trip";
        command.Parameters.AddWithValue("$trip", trip.Value.ToString("D"));
        string? status = await command.ExecuteScalarAsync() as string;
        await connection.CloseAsync();
        return status;
    }
}
