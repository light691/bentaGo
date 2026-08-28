using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Microsoft.Data.Sqlite;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Document persistence for <see cref="SqliteLedgerStore"/>: the five
/// fold-bound document sets (trips, sales+lines+payments, returns+lines,
/// loads+lines, voids) are read back into domain records, and the staged
/// open-attempt documents are INSERTed in the append transaction (D5).
/// </summary>
public sealed partial class SqliteLedgerStore
{
    private sealed record DocumentSets(
        IReadOnlyList<Trip> Trips,
        IReadOnlyList<Sale> Sales,
        IReadOnlyList<Return> Returns,
        IReadOnlyList<StockLoad> Loads,
        IReadOnlyList<SaleVoid> Voids);

    private void InsertTrip(SqliteConnection connection, Trip trip)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Trips" ("id", "peddler_id", "location_id", "created_utc", "created_by")
            VALUES ($id, $peddler, $location, $created, $createdBy)
            """;
        command.Parameters.AddWithValue("$id", D(trip.Id));
        command.Parameters.AddWithValue("$peddler", D(trip.Peddler));
        command.Parameters.AddWithValue("$location", D(trip.Location));
        command.Parameters.AddWithValue("$created", IsoUtc(trip.CreatedUtc));
        command.Parameters.AddWithValue("$createdBy", D(trip.CreatedBy));
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts ONLY the open-attempt documents (committed sets are already in the DB).</summary>
    private void InsertStagedDocuments(SqliteConnection connection)
    {
        foreach (Sale sale in _stagedSales)
        {
            InsertSale(connection, sale);
            foreach (SaleLine line in sale.Lines)
            {
                InsertSaleLine(connection, sale.Id, line);
            }

            foreach (PaymentLine payment in sale.Payments)
            {
                InsertPaymentLine(connection, sale.Id, payment);
            }
        }

        foreach (Return @return in _stagedReturns)
        {
            InsertReturn(connection, @return);
            foreach (ReturnLine line in @return.Lines)
            {
                InsertReturnLine(connection, @return.Id, line);
            }
        }

        foreach (StockLoad load in _stagedLoads)
        {
            InsertLoad(connection, load);
            foreach (StockLoadLine line in load.Lines)
            {
                InsertLoadLine(connection, load.Id, line);
            }
        }

        foreach (SaleVoid @void in _stagedVoids)
        {
            InsertVoid(connection, @void);
        }

        foreach (Adjustment adjustment in _stagedAdjustments)
        {
            InsertAdjustment(connection, adjustment);
        }

        foreach (PhysicalCount count in _stagedCounts)
        {
            InsertCount(connection, count);
            foreach (CountLine line in count.Lines)
            {
                InsertCountLine(connection, count.Id, line);
            }
        }

        foreach (DailyClose @close in _stagedCloses)
        {
            InsertClose(connection, @close);
        }
    }

    private static void InsertSale(SqliteConnection connection, Sale sale)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Sales" ("id", "trip_id", "peddler_id", "location_id", "receipt_number", "created_utc", "created_by", "total_centavos")
            VALUES ($id, $trip, $peddler, $location, $receipt, $created, $createdBy, $total)
            """;
        command.Parameters.AddWithValue("$id", D(sale.Id));
        command.Parameters.AddWithValue("$trip", D(sale.Trip));
        command.Parameters.AddWithValue("$peddler", D(sale.Peddler));
        command.Parameters.AddWithValue("$location", D(sale.Location));
        command.Parameters.AddWithValue("$receipt", sale.ReceiptNumber);
        command.Parameters.AddWithValue("$created", IsoUtc(sale.CreatedUtc));
        command.Parameters.AddWithValue("$createdBy", D(sale.CreatedBy));
        command.Parameters.AddWithValue("$total", sale.Total.Centavos);
        command.ExecuteNonQuery();
    }

    private static void InsertSaleLine(SqliteConnection connection, SaleId saleId, SaleLine line)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "SaleLines" ("id", "sale_id", "product_id", "sku", "product_name", "unit_price_centavos", "quantity", "discount_centavos")
            VALUES ($id, $sale, $product, $sku, $name, $price, $quantity, $discount)
            """;
        command.Parameters.AddWithValue("$id", D(line.Id));
        command.Parameters.AddWithValue("$sale", D(saleId));
        command.Parameters.AddWithValue("$product", D(line.ProductId));
        command.Parameters.AddWithValue("$sku", line.Sku);
        command.Parameters.AddWithValue("$name", line.ProductName);
        command.Parameters.AddWithValue("$price", line.UnitPrice.Centavos);
        command.Parameters.AddWithValue("$quantity", line.Quantity.Units);
        command.Parameters.AddWithValue("$discount", line.Discount.Centavos);
        command.ExecuteNonQuery();
    }

    private static void InsertPaymentLine(SqliteConnection connection, SaleId saleId, PaymentLine payment)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "PaymentLines" ("id", "sale_id", "method", "reference", "amount_centavos", "tendered_centavos", "change_centavos")
            VALUES ($id, $sale, $method, $reference, $amount, $tendered, $change)
            """;
        command.Parameters.AddWithValue("$id", D(payment.Id));
        command.Parameters.AddWithValue("$sale", D(saleId));
        command.Parameters.AddWithValue("$method", payment.Method.ToString());
        command.Parameters.AddWithValue("$reference", payment.Reference);
        command.Parameters.AddWithValue("$amount", payment.Amount.Centavos);
        command.Parameters.AddWithValue("$tendered", payment.Tendered.Centavos);
        command.Parameters.AddWithValue("$change", payment.Change.Centavos);
        command.ExecuteNonQuery();
    }

    private static void InsertReturn(SqliteConnection connection, Return @return)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Returns" ("id", "original_sale_id", "by", "when_utc")
            VALUES ($id, $sale, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(@return.Id));
        command.Parameters.AddWithValue("$sale", D(@return.OriginalSale));
        command.Parameters.AddWithValue("$by", D(@return.By));
        command.Parameters.AddWithValue("$when", IsoUtc(@return.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertReturnLine(SqliteConnection connection, ReturnId returnId, ReturnLine line)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "ReturnLines" ("return_id", "original_line", "quantity", "reason")
            VALUES ($return, $line, $quantity, $reason)
            """;
        command.Parameters.AddWithValue("$return", D(returnId));
        command.Parameters.AddWithValue("$line", D(line.OriginalLine));
        command.Parameters.AddWithValue("$quantity", line.Quantity.Units);
        command.Parameters.AddWithValue("$reason", line.Reason);
        command.ExecuteNonQuery();
    }

    private static void InsertLoad(SqliteConnection connection, StockLoad load)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "StockLoads" ("id", "load_number", "trip_id", "by", "when_utc")
            VALUES ($id, $number, $trip, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(load.Id));
        command.Parameters.AddWithValue("$number", load.LoadNumber);
        command.Parameters.AddWithValue("$trip", D(load.Trip));
        command.Parameters.AddWithValue("$by", D(load.By));
        command.Parameters.AddWithValue("$when", IsoUtc(load.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertLoadLine(SqliteConnection connection, LoadId loadId, StockLoadLine line)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "StockLoadLines" ("load_id", "product_id", "sku", "product_name", "quantity", "unit_cost_centavos")
            VALUES ($load, $product, $sku, $name, $quantity, $cost)
            """;
        command.Parameters.AddWithValue("$load", D(loadId));
        command.Parameters.AddWithValue("$product", D(line.ProductId));
        command.Parameters.AddWithValue("$sku", line.Sku);
        command.Parameters.AddWithValue("$name", line.ProductName);
        command.Parameters.AddWithValue("$quantity", line.Quantity.Units);
        command.Parameters.AddWithValue("$cost", line.UnitCost.Centavos);
        command.ExecuteNonQuery();
    }

    private static void InsertVoid(SqliteConnection connection, SaleVoid @void)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "SaleVoids" ("id", "original_sale_id", "reason", "by", "when_utc")
            VALUES ($id, $sale, $reason, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(@void.Id));
        command.Parameters.AddWithValue("$sale", D(@void.OriginalSaleId));
        command.Parameters.AddWithValue("$reason", @void.Reason);
        command.Parameters.AddWithValue("$by", D(@void.By));
        command.Parameters.AddWithValue("$when", IsoUtc(@void.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertAdjustment(SqliteConnection connection, Adjustment adjustment)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Adjustments" ("id", "product_id", "location_id", "reason", "by", "when_utc")
            VALUES ($id, $product, $location, $reason, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(adjustment.Id));
        command.Parameters.AddWithValue("$product", D(adjustment.ProductId));
        command.Parameters.AddWithValue("$location", D(adjustment.Location));
        command.Parameters.AddWithValue("$reason", adjustment.Reason);
        command.Parameters.AddWithValue("$by", D(adjustment.By));
        command.Parameters.AddWithValue("$when", IsoUtc(adjustment.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertCount(SqliteConnection connection, PhysicalCount count)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "PhysicalCounts" ("id", "location_id", "by", "when_utc")
            VALUES ($id, $location, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(count.Id));
        command.Parameters.AddWithValue("$location", D(count.Location));
        command.Parameters.AddWithValue("$by", D(count.By));
        command.Parameters.AddWithValue("$when", IsoUtc(count.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static void InsertCountLine(SqliteConnection connection, CountId countId, CountLine line)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "CountLines" ("count_id", "product_id", "observed", "notes")
            VALUES ($count, $product, $observed, $notes)
            """;
        command.Parameters.AddWithValue("$count", D(countId));
        command.Parameters.AddWithValue("$product", D(line.ProductId));
        command.Parameters.AddWithValue("$observed", line.Observed.Units);
        command.Parameters.AddWithValue("$notes", (object?)line.Notes ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertClose(SqliteConnection connection, DailyClose @close)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "DailyCloses" ("id", "trip_id", "expected_cash_centavos", "counted_cash_centavos", "variance_centavos", "notes", "by", "when_utc")
            VALUES ($id, $trip, $expected, $counted, $variance, $notes, $by, $when)
            """;
        command.Parameters.AddWithValue("$id", D(@close.Id));
        command.Parameters.AddWithValue("$trip", D(@close.Trip));
        command.Parameters.AddWithValue("$expected", @close.ExpectedCash.Centavos);
        command.Parameters.AddWithValue("$counted", @close.CountedCash.Centavos);
        command.Parameters.AddWithValue("$variance", @close.Variance.Centavos);
        command.Parameters.AddWithValue("$notes", (object?)@close.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$by", D(@close.By));
        command.Parameters.AddWithValue("$when", IsoUtc(@close.WhenUtc));
        command.ExecuteNonQuery();
    }

    private static DocumentSets ReadDocumentSets(SqliteConnection connection)
    {
        List<Trip> trips = ReadTrips(connection);
        Dictionary<SaleId, List<SaleLine>> lineGroups = ReadSaleLines(connection);
        Dictionary<SaleId, List<PaymentLine>> paymentGroups = ReadPaymentLines(connection);
        List<Sale> sales = ReadSales(connection, lineGroups, paymentGroups);
        List<Return> returns = ReadReturns(connection);
        List<StockLoad> loads = ReadLoads(connection);
        List<SaleVoid> voids = ReadVoids(connection);
        return new DocumentSets(trips, sales, returns, loads, voids);
    }

    private static List<Trip> ReadTrips(SqliteConnection connection)
    {
        var trips = new List<Trip>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"id\", \"peddler_id\", \"location_id\", \"created_utc\", \"created_by\" FROM \"Trips\" ORDER BY \"created_utc\", \"id\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            trips.Add(new Trip(
                new TripId(ParseGuid(reader.GetString(0))),
                new PeddlerId(ParseGuid(reader.GetString(1))),
                new LocationId(ParseGuid(reader.GetString(2))),
                ParseIso(reader.GetString(3)),
                new UserId(ParseGuid(reader.GetString(4)))));
        }

        return trips;
    }

    private static Dictionary<SaleId, List<SaleLine>> ReadSaleLines(SqliteConnection connection)
    {
        var groups = new Dictionary<SaleId, List<SaleLine>>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"id\", \"sale_id\", \"product_id\", \"sku\", \"product_name\", \"unit_price_centavos\", \"quantity\", \"discount_centavos\" FROM \"SaleLines\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var saleId = new SaleId(ParseGuid(reader.GetString(1)));
            if (!groups.TryGetValue(saleId, out List<SaleLine>? lines))
            {
                lines = new List<SaleLine>();
                groups[saleId] = lines;
            }

            lines.Add(new SaleLine(
                new SaleLineId(ParseGuid(reader.GetString(0))),
                new ProductId(ParseGuid(reader.GetString(2))),
                reader.GetString(3),
                reader.GetString(4),
                new Money(reader.GetInt64(5)),
                Quantity.Of(checked((int)reader.GetInt64(6))),
                new Money(reader.GetInt64(7))));
        }

        return groups;
    }

    private static Dictionary<SaleId, List<PaymentLine>> ReadPaymentLines(SqliteConnection connection)
    {
        var groups = new Dictionary<SaleId, List<PaymentLine>>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"id\", \"sale_id\", \"method\", \"reference\", \"amount_centavos\", \"tendered_centavos\", \"change_centavos\" FROM \"PaymentLines\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var saleId = new SaleId(ParseGuid(reader.GetString(1)));
            if (!groups.TryGetValue(saleId, out List<PaymentLine>? payments))
            {
                payments = new List<PaymentLine>();
                groups[saleId] = payments;
            }

            payments.Add(new PaymentLine(
                new PaymentLineId(ParseGuid(reader.GetString(0))),
                ParseMethod(reader.GetString(2)),
                reader.GetString(3),
                new Money(reader.GetInt64(4)),
                new Money(reader.GetInt64(5)),
                new Money(reader.GetInt64(6))));
        }

        return groups;
    }

    private static List<Sale> ReadSales(SqliteConnection connection, Dictionary<SaleId, List<SaleLine>> lineGroups, Dictionary<SaleId, List<PaymentLine>> paymentGroups)
    {
        var sales = new List<Sale>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"id\", \"trip_id\", \"peddler_id\", \"location_id\", \"receipt_number\", \"created_utc\", \"created_by\", \"total_centavos\" FROM \"Sales\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var saleId = new SaleId(ParseGuid(reader.GetString(0)));
            IReadOnlyList<SaleLine> lines = lineGroups.TryGetValue(saleId, out List<SaleLine>? foundLines) ? foundLines : Array.Empty<SaleLine>();
            IReadOnlyList<PaymentLine> payments = paymentGroups.TryGetValue(saleId, out List<PaymentLine>? foundPayments) ? foundPayments : Array.Empty<PaymentLine>();
            sales.Add(new Sale(
                saleId,
                new TripId(ParseGuid(reader.GetString(1))),
                new PeddlerId(ParseGuid(reader.GetString(2))),
                new LocationId(ParseGuid(reader.GetString(3))),
                reader.GetString(4),
                ParseIso(reader.GetString(5)),
                new UserId(ParseGuid(reader.GetString(6))),
                lines,
                payments,
                new Money(reader.GetInt64(7))));
        }

        return sales;
    }

    private static List<Return> ReadReturns(SqliteConnection connection)
    {
        var groups = new Dictionary<ReturnId, List<ReturnLine>>();
        using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.CommandText =
                "SELECT \"return_id\", \"original_line\", \"quantity\", \"reason\" FROM \"ReturnLines\" ORDER BY \"rowid\"";
            using SqliteDataReader lineReader = lineCommand.ExecuteReader();
            while (lineReader.Read())
            {
                var returnId = new ReturnId(ParseGuid(lineReader.GetString(0)));
                if (!groups.TryGetValue(returnId, out List<ReturnLine>? lines))
                {
                    lines = new List<ReturnLine>();
                    groups[returnId] = lines;
                }

                lines.Add(new ReturnLine(
                    new SaleLineId(ParseGuid(lineReader.GetString(1))),
                    Quantity.Of(checked((int)lineReader.GetInt64(2))),
                    lineReader.GetString(3)));
            }
        }

        var returns = new List<Return>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"id\", \"original_sale_id\", \"by\", \"when_utc\" FROM \"Returns\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var returnId = new ReturnId(ParseGuid(reader.GetString(0)));
            IReadOnlyList<ReturnLine> lines = groups.TryGetValue(returnId, out List<ReturnLine>? found) ? found : Array.Empty<ReturnLine>();
            returns.Add(new Return(
                returnId,
                new SaleId(ParseGuid(reader.GetString(1))),
                lines,
                new UserId(ParseGuid(reader.GetString(2))),
                ParseIso(reader.GetString(3))));
        }

        return returns;
    }

    private static List<StockLoad> ReadLoads(SqliteConnection connection)
    {
        var groups = new Dictionary<LoadId, List<StockLoadLine>>();
        using (var lineCommand = connection.CreateCommand())
        {
            lineCommand.CommandText =
                "SELECT \"load_id\", \"product_id\", \"sku\", \"product_name\", \"quantity\", \"unit_cost_centavos\" FROM \"StockLoadLines\" ORDER BY \"rowid\"";
            using SqliteDataReader lineReader = lineCommand.ExecuteReader();
            while (lineReader.Read())
            {
                var loadId = new LoadId(ParseGuid(lineReader.GetString(0)));
                if (!groups.TryGetValue(loadId, out List<StockLoadLine>? lines))
                {
                    lines = new List<StockLoadLine>();
                    groups[loadId] = lines;
                }

                lines.Add(new StockLoadLine(
                    new ProductId(ParseGuid(lineReader.GetString(1))),
                    lineReader.GetString(2),
                    lineReader.GetString(3),
                    Quantity.Of(checked((int)lineReader.GetInt64(4))),
                    new Money(lineReader.GetInt64(5))));
            }
        }

        var loads = new List<StockLoad>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"id\", \"load_number\", \"trip_id\", \"by\", \"when_utc\" FROM \"StockLoads\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            var loadId = new LoadId(ParseGuid(reader.GetString(0)));
            IReadOnlyList<StockLoadLine> lines = groups.TryGetValue(loadId, out List<StockLoadLine>? found) ? found : Array.Empty<StockLoadLine>();
            loads.Add(new StockLoad(
                loadId,
                reader.GetString(1),
                new TripId(ParseGuid(reader.GetString(2))),
                lines,
                new UserId(ParseGuid(reader.GetString(3))),
                ParseIso(reader.GetString(4))));
        }

        return loads;
    }

    private static List<SaleVoid> ReadVoids(SqliteConnection connection)
    {
        var voids = new List<SaleVoid>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"id\", \"original_sale_id\", \"reason\", \"by\", \"when_utc\" FROM \"SaleVoids\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            voids.Add(new SaleVoid(
                new VoidId(ParseGuid(reader.GetString(0))),
                new SaleId(ParseGuid(reader.GetString(1))),
                reader.GetString(2),
                new UserId(ParseGuid(reader.GetString(3))),
                ParseIso(reader.GetString(4))));
        }

        return voids;
    }
}
