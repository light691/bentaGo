using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Microsoft.Data.Sqlite;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Durable balance projection writing for <see cref="SqliteLedgerStore"/>
/// (BG-003 D3): one projection path — every written value comes from the
/// validated fold state, never from SQL aggregates. Append writes only the
/// batch's affected keys; Rebuild deletes and rewrites everything, so after a
/// rebuild the projections equal the hot state exactly (lane 6).
/// </summary>
public sealed partial class SqliteLedgerStore
{
    private void WriteProjections(SqliteConnection connection, LedgerState state, IReadOnlyList<Movement> batchMovements)
    {
        var cells = new HashSet<(ProductId, LocationId)>();
        var observedCells = new HashSet<(ProductId, LocationId)>();
        var funds = new HashSet<Fund>();
        var trips = new HashSet<TripId>();
        bool resolveAllTrips = false;

        foreach (Movement movement in batchMovements)
        {
            switch (movement)
            {
                case OpeningStock m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case TransferOut m:
                    cells.Add((m.ProductId, m.LocationId));
                    AddLoadTrip(trips, m.Document);
                    break;
                case TransferIn m:
                    cells.Add((m.ProductId, m.LocationId));
                    AddLoadTrip(trips, m.Document);
                    break;
                case SaleIssue m:
                    cells.Add((m.ProductId, m.LocationId));
                    AddSaleTrip(trips, m.Document);
                    break;
                case SaleIssueRevoke m:
                    cells.Add((m.ProductId, m.LocationId));
                    resolveAllTrips = true; // original issue predates this batch
                    break;
                case ReturnToStock m:
                    cells.Add((m.ProductId, m.LocationId));
                    resolveAllTrips = true; // original sale predates this batch
                    break;
                case Damage m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case Loss m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case AdjustmentIncrease m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case AdjustmentDecrease m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case Receipt m:
                    if (m.Method.FundOf() is { } fund)
                    {
                        funds.Add(fund);
                    }

                    AddSaleTrip(trips, m.Document);
                    break;
                case ReceiptRefund m:
                    if (m.Method.FundOf() is { } refundFund)
                    {
                        funds.Add(refundFund);
                    }

                    resolveAllTrips = true; // original receipt predates this batch
                    break;
                case ReceiptRevoke m:
                    if (m.Method.FundOf() is { } revokeFund)
                    {
                        funds.Add(revokeFund);
                    }

                    resolveAllTrips = true; // original receipt predates this batch
                    break;
                case CountObserved m:
                    cells.Add((m.ProductId, m.LocationId));
                    observedCells.Add((m.ProductId, m.LocationId));
                    break;
                case TripClose m:
                    trips.Add(m.TripId);
                    break;
                case TripReopen m:
                    trips.Add(m.TripId);
                    break;
            }
        }

        if (resolveAllTrips)
        {
            trips.Clear();
            foreach (TripId trip in ReadAllTripIds(connection))
            {
                trips.Add(trip);
            }
        }

        foreach ((ProductId product, LocationId location) in cells)
        {
            UpsertCellBalance(connection, state, product, location);
            UpsertCellBreakdown(connection, state, product, location);
        }

        foreach (Fund fund in funds)
        {
            UpsertFundBalance(connection, state, fund);
        }

        foreach ((ProductId product, LocationId location) in observedCells)
        {
            UpsertObservedQuantity(connection, state, product, location);
        }

        foreach (TripId trip in trips)
        {
            UpsertTripState(connection, state, trip);
        }
    }

    private static void RewriteAllProjections(
        SqliteConnection connection,
        LedgerState state,
        IReadOnlyList<Movement> movements,
        IReadOnlyList<Trip> trips)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM "CellBalances";
                DELETE FROM "FundBalances";
                DELETE FROM "ObservedQuantities";
                DELETE FROM "TripStates";
                DELETE FROM "CellBreakdown";
                """;
            command.ExecuteNonQuery();
        }

        var cells = new HashSet<(ProductId, LocationId)>();
        var observedCells = new HashSet<(ProductId, LocationId)>();
        var funds = new HashSet<Fund>();
        foreach (Movement movement in movements)
        {
            switch (movement)
            {
                case OpeningStock m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case TransferOut m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case TransferIn m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case SaleIssue m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case SaleIssueRevoke m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case ReturnToStock m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case Damage m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case Loss m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case AdjustmentIncrease m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case AdjustmentDecrease m:
                    cells.Add((m.ProductId, m.LocationId));
                    break;
                case Receipt m:
                    if (m.Method.FundOf() is { } fund)
                    {
                        funds.Add(fund);
                    }

                    break;
                case ReceiptRefund m:
                    if (m.Method.FundOf() is { } refundFund)
                    {
                        funds.Add(refundFund);
                    }

                    break;
                case ReceiptRevoke m:
                    if (m.Method.FundOf() is { } revokeFund)
                    {
                        funds.Add(revokeFund);
                    }

                    break;
                case CountObserved m:
                    cells.Add((m.ProductId, m.LocationId));
                    observedCells.Add((m.ProductId, m.LocationId));
                    break;
            }
        }

        foreach ((ProductId product, LocationId location) in cells)
        {
            InsertCellBalance(connection, state, product, location);
            InsertCellBreakdown(connection, state, product, location);
        }

        foreach (Fund fund in funds)
        {
            InsertFundBalance(connection, state, fund);
        }

        foreach ((ProductId product, LocationId location) in observedCells)
        {
            InsertObservedQuantity(connection, state, product, location);
        }

        foreach (Trip trip in trips)
        {
            InsertTripState(connection, state, trip.Id);
        }
    }

    // ------------------------------------------------------------------
    // Affected-key derivation
    // ------------------------------------------------------------------

    private void AddSaleTrip(HashSet<TripId> trips, DocumentRef reference)
    {
        if (reference is not SaleRef saleRef)
        {
            return;
        }

        foreach (Sale sale in _docsSales)
        {
            if (sale.Id == saleRef.Id)
            {
                trips.Add(sale.Trip);
                return;
            }
        }

        foreach (Sale sale in _stagedSales)
        {
            if (sale.Id == saleRef.Id)
            {
                trips.Add(sale.Trip);
                return;
            }
        }
    }

    private void AddLoadTrip(HashSet<TripId> trips, DocumentRef reference)
    {
        if (reference is not LoadRef loadRef)
        {
            return;
        }

        foreach (StockLoad load in _docsLoads)
        {
            if (load.Id == loadRef.Id)
            {
                trips.Add(load.Trip);
                return;
            }
        }

        foreach (StockLoad load in _stagedLoads)
        {
            if (load.Id == loadRef.Id)
            {
                trips.Add(load.Trip);
                return;
            }
        }
    }

    private static List<TripId> ReadAllTripIds(SqliteConnection connection)
    {
        var trips = new List<TripId>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"id\" FROM \"Trips\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            trips.Add(new TripId(ParseGuid(reader.GetString(0))));
        }

        return trips;
    }

    // ------------------------------------------------------------------
    // Upserts (append path — the batch's affected keys only)
    // ------------------------------------------------------------------

    private static void UpsertCellBalance(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "CellBalances" ("product_id", "location_id", "units")
            VALUES ($product, $location, $units)
            ON CONFLICT ("product_id", "location_id") DO UPDATE SET "units" = excluded."units"
            """;
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$units", state.StockOnHand(product, location).Units);
        command.ExecuteNonQuery();
    }

    private static void UpsertCellBreakdown(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        InventoryBreakdown b = state.Breakdown(product, location);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "CellBreakdown" ("product_id", "location_id", "starting", "loaded", "sold", "returned", "damaged", "lost", "adjusted", "ending", "observed", "variance")
            VALUES ($product, $location, $starting, $loaded, $sold, $returned, $damaged, $lost, $adjusted, $ending, $observed, $variance)
            ON CONFLICT ("product_id", "location_id") DO UPDATE SET
                "starting" = excluded."starting", "loaded" = excluded."loaded", "sold" = excluded."sold",
                "returned" = excluded."returned", "damaged" = excluded."damaged", "lost" = excluded."lost",
                "adjusted" = excluded."adjusted", "ending" = excluded."ending",
                "observed" = excluded."observed", "variance" = excluded."variance"
            """;
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$starting", b.Starting);
        command.Parameters.AddWithValue("$loaded", b.Loaded);
        command.Parameters.AddWithValue("$sold", b.Sold);
        command.Parameters.AddWithValue("$returned", b.Returned);
        command.Parameters.AddWithValue("$damaged", b.Damaged);
        command.Parameters.AddWithValue("$lost", b.Lost);
        command.Parameters.AddWithValue("$adjusted", b.Adjusted);
        command.Parameters.AddWithValue("$ending", b.Ending);
        command.Parameters.AddWithValue("$observed", (object?)b.Observed?.Units ?? DBNull.Value);
        command.Parameters.AddWithValue("$variance", (object?)b.Variance ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpsertFundBalance(SqliteConnection connection, LedgerState state, Fund fund)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "FundBalances" ("fund", "centavos")
            VALUES ($fund, $centavos)
            ON CONFLICT ("fund") DO UPDATE SET "centavos" = excluded."centavos"
            """;
        command.Parameters.AddWithValue("$fund", fund.ToString());
        command.Parameters.AddWithValue("$centavos", state.FundsOnHand(fund).Centavos);
        command.ExecuteNonQuery();
    }

    private static void UpsertObservedQuantity(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "ObservedQuantities" ("product_id", "location_id", "units")
            VALUES ($product, $location, $units)
            ON CONFLICT ("product_id", "location_id") DO UPDATE SET "units" = excluded."units"
            """;
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$units", state.ObservedUnits(product, location).Units);
        command.ExecuteNonQuery();
    }

    private static void UpsertTripState(SqliteConnection connection, LedgerState state, TripId trip)
    {
        TripState tripState = state.Trip(trip);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "TripStates" ("trip_id", "status", "opened_utc", "closed_utc")
            VALUES ($trip, $status, $opened, $closed)
            ON CONFLICT ("trip_id") DO UPDATE SET
                "status" = excluded."status", "opened_utc" = excluded."opened_utc", "closed_utc" = excluded."closed_utc"
            """;
        command.Parameters.AddWithValue("$trip", D(trip));
        command.Parameters.AddWithValue("$status", tripState.Status.ToString());
        command.Parameters.AddWithValue("$opened", (object?)NullableIso(tripState.OpenedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$closed", (object?)NullableIso(tripState.ClosedUtc) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // Inserts (rebuild path — full rewrite)
    // ------------------------------------------------------------------

    private static void InsertCellBalance(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"CellBalances\" (\"product_id\", \"location_id\", \"units\") VALUES ($product, $location, $units)";
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$units", state.StockOnHand(product, location).Units);
        command.ExecuteNonQuery();
    }

    private static void InsertCellBreakdown(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        InventoryBreakdown b = state.Breakdown(product, location);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "CellBreakdown" ("product_id", "location_id", "starting", "loaded", "sold", "returned", "damaged", "lost", "adjusted", "ending", "observed", "variance")
            VALUES ($product, $location, $starting, $loaded, $sold, $returned, $damaged, $lost, $adjusted, $ending, $observed, $variance)
            """;
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$starting", b.Starting);
        command.Parameters.AddWithValue("$loaded", b.Loaded);
        command.Parameters.AddWithValue("$sold", b.Sold);
        command.Parameters.AddWithValue("$returned", b.Returned);
        command.Parameters.AddWithValue("$damaged", b.Damaged);
        command.Parameters.AddWithValue("$lost", b.Lost);
        command.Parameters.AddWithValue("$adjusted", b.Adjusted);
        command.Parameters.AddWithValue("$ending", b.Ending);
        command.Parameters.AddWithValue("$observed", (object?)b.Observed?.Units ?? DBNull.Value);
        command.Parameters.AddWithValue("$variance", (object?)b.Variance ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertFundBalance(SqliteConnection connection, LedgerState state, Fund fund)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"FundBalances\" (\"fund\", \"centavos\") VALUES ($fund, $centavos)";
        command.Parameters.AddWithValue("$fund", fund.ToString());
        command.Parameters.AddWithValue("$centavos", state.FundsOnHand(fund).Centavos);
        command.ExecuteNonQuery();
    }

    private static void InsertObservedQuantity(SqliteConnection connection, LedgerState state, ProductId product, LocationId location)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"ObservedQuantities\" (\"product_id\", \"location_id\", \"units\") VALUES ($product, $location, $units)";
        command.Parameters.AddWithValue("$product", D(product));
        command.Parameters.AddWithValue("$location", D(location));
        command.Parameters.AddWithValue("$units", state.ObservedUnits(product, location).Units);
        command.ExecuteNonQuery();
    }

    private static void InsertTripState(SqliteConnection connection, LedgerState state, TripId trip)
    {
        TripState tripState = state.Trip(trip);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO \"TripStates\" (\"trip_id\", \"status\", \"opened_utc\", \"closed_utc\") VALUES ($trip, $status, $opened, $closed)";
        command.Parameters.AddWithValue("$trip", D(trip));
        command.Parameters.AddWithValue("$status", tripState.Status.ToString());
        command.Parameters.AddWithValue("$opened", (object?)NullableIso(tripState.OpenedUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$closed", (object?)NullableIso(tripState.ClosedUtc) ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? NullableIso(DateTimeOffset? when) =>
        when is null ? null : IsoUtc(when.Value);
}
