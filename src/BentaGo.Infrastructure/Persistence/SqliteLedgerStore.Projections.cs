using BentaGo.Core.Domain;
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

        foreach (Movement movement in batchMovements)
        {
            switch (movement)
            {
                case GoodsReceived m:
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
                case FundIn m:
                    if (m.Method.FundOf() is { } inFund)
                    {
                        funds.Add(inFund);
                    }

                    break;
                case FundOut m:
                    if (m.Method.FundOf() is { } outFund)
                    {
                        funds.Add(outFund);
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
            UpsertCellBalance(connection, state, product, location);
        }

        foreach (Fund fund in funds)
        {
            UpsertFundBalance(connection, state, fund);
        }

        foreach ((ProductId product, LocationId location) in observedCells)
        {
            UpsertObservedQuantity(connection, state, product, location);
        }
    }

    private static void RewriteAllProjections(
        SqliteConnection connection,
        LedgerState state,
        IReadOnlyList<Movement> movements)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM "CellBalances";
                DELETE FROM "FundBalances";
                DELETE FROM "ObservedQuantities";
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
                case GoodsReceived m:
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
                case FundIn m:
                    if (m.Method.FundOf() is { } inFund)
                    {
                        funds.Add(inFund);
                    }

                    break;
                case FundOut m:
                    if (m.Method.FundOf() is { } outFund)
                    {
                        funds.Add(outFund);
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
        }

        foreach (Fund fund in funds)
        {
            InsertFundBalance(connection, state, fund);
        }

        foreach ((ProductId product, LocationId location) in observedCells)
        {
            InsertObservedQuantity(connection, state, product, location);
        }
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
}
