using System.Globalization;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using Microsoft.Data.Sqlite;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Movement-row mapping for <see cref="SqliteLedgerStore"/>: encode a sealed
/// domain movement into the single Movements row (kind + normalized payload
/// columns per BG-003 §7) and decode rows back through the same switch. The
/// decode is the read-boundary legality check: an unknown kind, unknown
/// document kind, or illegal column set surfaces as
/// <see cref="LedgerCorruptException"/> instead of corrupting the fold.
/// </summary>
public sealed partial class SqliteLedgerStore
{
    private sealed record HeadWatermark(long LastSequence, long DocumentsVersion);

    private static void InsertMovement(SqliteConnection connection, Movement movement)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id",
                "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method",
                "amount_centavos", "reverses", "reason", "observed_quantity")
            VALUES ($seq, $kind, $when, $actor, $docKind, $docId, $product, $location, $quantity,
                $saleLine, $paymentLine, $method, $amount, $reverses, $reason, $observed)
            """;

        command.Parameters.AddWithValue("$seq", movement.Sequence);
        command.Parameters.AddWithValue("$kind", movement.Kind.ToString());
        command.Parameters.AddWithValue("$when", IsoUtc(movement.WhenUtc));
        command.Parameters.AddWithValue("$actor", D(movement.ActorId));
        command.Parameters.AddWithValue("$docKind", DocKindName(movement.Document));
        command.Parameters.AddWithValue("$docId", DocId(movement.Document));

        EncodePayload(movement, out string? product, out string? location, out long? quantity,
            out string? saleLine, out string? paymentLine, out string? method, out long? amount,
            out long? reverses, out string? reason, out long? observed);

        command.Parameters.AddWithValue("$product", (object?)product ?? DBNull.Value);
        command.Parameters.AddWithValue("$location", (object?)location ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", (object?)quantity ?? DBNull.Value);
        command.Parameters.AddWithValue("$saleLine", (object?)saleLine ?? DBNull.Value);
        command.Parameters.AddWithValue("$paymentLine", (object?)paymentLine ?? DBNull.Value);
        command.Parameters.AddWithValue("$method", (object?)method ?? DBNull.Value);
        command.Parameters.AddWithValue("$amount", (object?)amount ?? DBNull.Value);
        command.Parameters.AddWithValue("$reverses", (object?)reverses ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", (object?)observed ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private List<Movement> ReadMovements(SqliteConnection connection)
    {
        var movements = new List<Movement>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"Movements\" ORDER BY \"sequence\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            movements.Add(DecodeMovement(reader));
        }

        return movements;
    }

    private static Movement DecodeMovement(SqliteDataReader reader)
    {
        try
        {
            long sequence = reader.GetInt64(0);
            string kind = reader.GetString(1);
            DateTimeOffset whenUtc = ParseIso(reader.GetString(2));
            var actor = new UserId(ParseGuid(reader.GetString(3)));
            DocumentRef document = DecodeDocumentRef(reader.GetString(4), reader.GetString(5));

            string? product = NullableString(reader, 6);
            string? location = NullableString(reader, 7);
            long? quantity = NullableLong(reader, 8);
            string? saleLine = NullableString(reader, 9);
            string? paymentLine = NullableString(reader, 10);
            string? method = NullableString(reader, 11);
            long? amount = NullableLong(reader, 12);
            long? reverses = NullableLong(reader, 13);
            string? reason = NullableString(reader, 14);
            long? observed = NullableLong(reader, 15);

            return kind switch
            {
                "GoodsReceived" => Moves.GoodsReceived(sequence, whenUtc, actor, RequireRef<GoodsRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity)))),
                "SaleIssue" => Moves.SaleIssue(sequence, whenUtc, actor, RequireRef<SaleRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    new SaleLineId(ParseGuid(RequireColumn(kind, "sale_line_id", saleLine)))),
                "SaleIssueRevoke" => Moves.SaleIssueRevoke(sequence, whenUtc, actor, RequireRef<VoidRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    RequireColumn(kind, "reverses", reverses)),
                "ReturnToStock" => Moves.ReturnToStock(sequence, whenUtc, actor, RequireRef<ReturnRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    new SaleLineId(ParseGuid(RequireColumn(kind, "sale_line_id", saleLine))),
                    RequireColumn(kind, "reverses", reverses)),
                "Damage" => Moves.Damage(sequence, whenUtc, actor, RequireRef<AdjustmentRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    RequireColumn(kind, "reason", reason)),
                "Loss" => Moves.Loss(sequence, whenUtc, actor, RequireRef<AdjustmentRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    RequireColumn(kind, "reason", reason)),
                "AdjustmentIncrease" => Moves.AdjustmentIncrease(sequence, whenUtc, actor, RequireRef<AdjustmentRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    RequireColumn(kind, "reason", reason)),
                "AdjustmentDecrease" => Moves.AdjustmentDecrease(sequence, whenUtc, actor, RequireRef<AdjustmentRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "quantity", quantity))),
                    RequireColumn(kind, "reason", reason)),
                "Receipt" => Moves.Receipt(sequence, whenUtc, actor, RequireRef<SaleRef>(kind, document),
                    ParseMethod(RequireColumn(kind, "method", method)),
                    new Money(RequireColumn(kind, "amount_centavos", amount)),
                    new PaymentLineId(ParseGuid(RequireColumn(kind, "payment_line_id", paymentLine)))),
                "ReceiptRefund" => Moves.ReceiptRefund(sequence, whenUtc, actor, RequireRef<ReturnRef>(kind, document),
                    ParseMethod(RequireColumn(kind, "method", method)),
                    new Money(RequireColumn(kind, "amount_centavos", amount)),
                    new PaymentLineId(ParseGuid(RequireColumn(kind, "payment_line_id", paymentLine))),
                    RequireColumn(kind, "reverses", reverses)),
                "ReceiptRevoke" => Moves.ReceiptRevoke(sequence, whenUtc, actor, RequireRef<VoidRef>(kind, document),
                    ParseMethod(RequireColumn(kind, "method", method)),
                    new Money(RequireColumn(kind, "amount_centavos", amount)),
                    new PaymentLineId(ParseGuid(RequireColumn(kind, "payment_line_id", paymentLine))),
                    RequireColumn(kind, "reverses", reverses)),
                "CountObserved" => Moves.CountObserved(sequence, whenUtc, actor, RequireRef<CountRef>(kind, document),
                    new ProductId(ParseGuid(RequireColumn(kind, "product_id", product))),
                    new LocationId(ParseGuid(RequireColumn(kind, "location_id", location))),
                    Quantity.Of(checked((int)RequireColumn(kind, "observed_quantity", observed)))),
                "FundIn" => Moves.FundIn(sequence, whenUtc, actor, RequireRef<FundRef>(kind, document),
                    ParseMethod(RequireColumn(kind, "method", method)),
                    new Money(RequireColumn(kind, "amount_centavos", amount)),
                    RequireColumn(kind, "reason", reason)),
                "FundOut" => Moves.FundOut(sequence, whenUtc, actor, RequireRef<FundRef>(kind, document),
                    ParseMethod(RequireColumn(kind, "method", method)),
                    new Money(RequireColumn(kind, "amount_centavos", amount)),
                    RequireColumn(kind, "reason", reason)),
                _ => throw new LedgerCorruptException($"unknown movement kind '{kind}' at sequence {sequence}"),
            };
        }
        catch (Exception ex) when (ex is LedgerCorruptException or ArgumentException or FormatException
            or OverflowException or InvalidCastException or InvalidOperationException)
        {
            throw new LedgerCorruptException(
                $"cannot decode movement row: {ex.Message}");
        }
    }

    private static void EncodePayload(
        Movement movement,
        out string? product, out string? location, out long? quantity,
        out string? saleLine, out string? paymentLine, out string? method, out long? amount,
        out long? reverses, out string? reason, out long? observed)
    {
        switch (movement)
        {
            case GoodsReceived m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                saleLine = paymentLine = method = reason = null;
                amount = reverses = observed = null;
                break;
            case SaleIssue m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                saleLine = D(m.SaleLineId);
                paymentLine = method = reason = null;
                amount = reverses = observed = null;
                break;
            case SaleIssueRevoke m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                reverses = m.Reverses;
                saleLine = paymentLine = method = reason = null;
                amount = observed = null;
                break;
            case ReturnToStock m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                saleLine = D(m.SaleLineId);
                reverses = m.Reverses;
                paymentLine = method = reason = null;
                amount = observed = null;
                break;
            case Damage m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                reason = m.Reason;
                saleLine = paymentLine = method = null;
                amount = reverses = observed = null;
                break;
            case Loss m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                reason = m.Reason;
                saleLine = paymentLine = method = null;
                amount = reverses = observed = null;
                break;
            case AdjustmentIncrease m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                reason = m.Reason;
                saleLine = paymentLine = method = null;
                amount = reverses = observed = null;
                break;
            case AdjustmentDecrease m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                quantity = m.Quantity.Units;
                reason = m.Reason;
                saleLine = paymentLine = method = null;
                amount = reverses = observed = null;
                break;
            case Receipt m:
                paymentLine = D(m.PaymentLineId);
                method = m.Method.ToString();
                amount = m.Amount.Centavos;
                product = location = saleLine = reason = null;
                quantity = reverses = observed = null;
                break;
            case ReceiptRefund m:
                paymentLine = D(m.PaymentLineId);
                method = m.Method.ToString();
                amount = m.Amount.Centavos;
                reverses = m.Reverses;
                product = location = saleLine = reason = null;
                quantity = observed = null;
                break;
            case ReceiptRevoke m:
                paymentLine = D(m.PaymentLineId);
                method = m.Method.ToString();
                amount = m.Amount.Centavos;
                reverses = m.Reverses;
                product = location = saleLine = reason = null;
                quantity = observed = null;
                break;
            case FundIn m:
                method = m.Method.ToString();
                amount = m.Amount.Centavos;
                reason = m.Reason;
                product = location = saleLine = paymentLine = null;
                quantity = reverses = observed = null;
                break;
            case FundOut m:
                method = m.Method.ToString();
                amount = m.Amount.Centavos;
                reason = m.Reason;
                product = location = saleLine = paymentLine = null;
                quantity = reverses = observed = null;
                break;
            case CountObserved m:
                product = D(m.ProductId);
                location = D(m.LocationId);
                observed = m.ObservedQuantity.Units;
                quantity = null;
                saleLine = paymentLine = method = reason = null;
                amount = reverses = null;
                break;
            default:
                throw new LedgerCorruptException($"unknown movement kind '{movement.GetType().Name}'");
        }
    }

    private static string DocKindName(DocumentRef reference) => reference switch
    {
        GoodsRef => "Goods",
        SaleRef => "Sale",
        VoidRef => "Void",
        ReturnRef => "Return",
        AdjustmentRef => "Adjustment",
        CountRef => "Count",
        FundRef => "Fund",
        _ => throw new LedgerCorruptException($"unknown document reference '{reference.GetType().Name}'"),
    };

    private static string DocId(DocumentRef reference) => reference switch
    {
        GoodsRef r => D(r.Id),
        SaleRef r => D(r.Id),
        VoidRef r => D(r.Id),
        ReturnRef r => D(r.Id),
        AdjustmentRef r => D(r.Id),
        CountRef r => D(r.Id),
        FundRef r => D(r.Id),
        _ => throw new LedgerCorruptException($"unknown document reference '{reference.GetType().Name}'"),
    };

    private static DocumentRef DecodeDocumentRef(string docKind, string docId)
    {
        Guid id = ParseGuid(docId);
        return docKind switch
        {
            "Goods" => new GoodsRef(new GoodsId(id)),
            "Sale" => new SaleRef(new SaleId(id)),
            "Void" => new VoidRef(new VoidId(id)),
            "Return" => new ReturnRef(new ReturnId(id)),
            "Adjustment" => new AdjustmentRef(new AdjustmentId(id)),
            "Count" => new CountRef(new CountId(id)),
            "Fund" => new FundRef(new FundEntryId(id)),
            _ => throw new LedgerCorruptException($"unknown document kind '{docKind}'"),
        };
    }

    private static HeadWatermark ReadHead(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"last_sequence\", \"documents_version\" FROM \"LedgerHead\" WHERE \"id\" = 1";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new LedgerCorruptException("LedgerHead watermark row is missing");
        }

        return new HeadWatermark(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static void UpdateHead(SqliteConnection connection, long lastSequence, long documentsVersion)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE \"LedgerHead\" SET \"last_sequence\" = $last, \"documents_version\" = $version WHERE \"id\" = 1";
        command.Parameters.AddWithValue("$last", lastSequence);
        command.Parameters.AddWithValue("$version", documentsVersion);
        command.ExecuteNonQuery();
    }

    private static void BumpDocumentsVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE \"LedgerHead\" SET \"documents_version\" = \"documents_version\" + 1 WHERE \"id\" = 1";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts an audit row WITHOUT a caller-supplied sequence: the
    /// AuditEvents PK on Sequence is the SQLite rowid identity (BG-005a),
    /// so the row gets the next identity and <c>last_insert_rowid()</c>
    /// returns it to the caller. The -1 placeholder is never stored.
    /// </summary>
    private static long InsertAudit(SqliteConnection connection, AuditEvent audit)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO "AuditEvents" ("actor_id", "when_utc", "category", "message", "doc_kind", "doc_id", "terminal", "reason")
            VALUES ($actor, $when, $category, $message, $docKind, $docId, $terminal, $reason)
            """;
        command.Parameters.AddWithValue("$actor", D(audit.Actor));
        command.Parameters.AddWithValue("$when", IsoUtc(audit.WhenUtc));
        command.Parameters.AddWithValue("$category", audit.Category);
        command.Parameters.AddWithValue("$message", audit.Message);
        command.Parameters.AddWithValue("$terminal", (object?)audit.Terminal ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)audit.Reason ?? DBNull.Value);
        if (audit.Ref is null)
        {
            command.Parameters.AddWithValue("$docKind", DBNull.Value);
            command.Parameters.AddWithValue("$docId", DBNull.Value);
        }
        else
        {
            command.Parameters.AddWithValue("$docKind", DocKindName(audit.Ref));
            command.Parameters.AddWithValue("$docId", DocId(audit.Ref));
        }

        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCommand.ExecuteScalar());
    }

    /// <summary>
    /// Reads the audit stream. A store can be opened over a PRE-BG005 schema
    /// (the lane-9 prior-schema fixture renders before the upgrade), where
    /// AuditEvents has no identity PK and no terminal/reason columns — the
    /// SELECT adapts to whichever shape the table actually has.
    /// </summary>
    private List<AuditEvent> ReadAudits(SqliteConnection connection)
    {
        bool hardened = TableHasColumn(connection, "AuditEvents", "terminal");
        string columns = hardened
            ? "\"sequence\", \"actor_id\", \"when_utc\", \"category\", \"message\", \"doc_kind\", \"doc_id\", \"terminal\", \"reason\""
            : "\"sequence\", \"actor_id\", \"when_utc\", \"category\", \"message\", \"doc_kind\", \"doc_id\"";

        var audits = new List<AuditEvent>();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {columns} FROM \"AuditEvents\" ORDER BY \"rowid\"";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? docKind = reader.IsDBNull(5) ? null : reader.GetString(5);
            string? docId = reader.IsDBNull(6) ? null : reader.GetString(6);
            DocumentRef? reference = docKind is null ? null : DecodeDocumentRef(docKind, docId!);
            audits.Add(new AuditEvent(
                reader.GetInt64(0),
                new UserId(ParseGuid(reader.GetString(1))),
                ParseIso(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reference,
                hardened && !reader.IsDBNull(7) ? reader.GetString(7) : null,
                hardened && !reader.IsDBNull(8) ? reader.GetString(8) : null));
        }

        return audits;
    }

    private static bool TableHasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM pragma_table_info($table) WHERE \"name\" = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static void InsertIdempotency(SqliteConnection connection, string key, string payload)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"IdempotencyKeys\" (\"key\", \"payload\") VALUES ($key, $payload)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    private static string? ReadIdempotencyPayload(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"payload\" FROM \"IdempotencyKeys\" WHERE \"key\" = $key";
        command.Parameters.AddWithValue("$key", key);
        object? result = command.ExecuteScalar();
        return result as string;
    }

    // ------------------------------------------------------------------
    // Column helpers
    // ------------------------------------------------------------------

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string RequireColumn(string kind, string column, string? value) =>
        value ?? throw new LedgerCorruptException($"{kind} is missing '{column}'");

    private static long RequireColumn(string kind, string column, long? value) =>
        value ?? throw new LedgerCorruptException($"{kind} is missing '{column}'");

    private static T RequireRef<T>(string kind, DocumentRef reference)
        where T : DocumentRef =>
        reference as T ?? throw new LedgerCorruptException($"{kind} carries a {reference.GetType().Name} document; expected {typeof(T).Name}");

    private static PaymentMethod ParseMethod(string method) =>
        Enum.TryParse<PaymentMethod>(method, out PaymentMethod parsed)
            ? parsed
            : throw new LedgerCorruptException($"unknown payment method '{method}'");

    private static string IsoUtc(DateTimeOffset when) =>
        when.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static Guid ParseGuid(string value) => Guid.Parse(value);

    private static string D(UserId value) => value.Value.ToString("D");
    private static string D(DayId value) => value.Value.ToString("D");
    private static string D(ProductId value) => value.Value.ToString("D");
    private static string D(LocationId value) => value.Value.ToString("D");
    private static string D(SaleId value) => value.Value.ToString("D");
    private static string D(SaleLineId value) => value.Value.ToString("D");
    private static string D(VoidId value) => value.Value.ToString("D");
    private static string D(ReturnId value) => value.Value.ToString("D");
    private static string D(AdjustmentId value) => value.Value.ToString("D");
    private static string D(CountId value) => value.Value.ToString("D");
    private static string D(CloseId value) => value.Value.ToString("D");
    private static string D(GoodsId value) => value.Value.ToString("D");
    private static string D(FundEntryId value) => value.Value.ToString("D");
    private static string D(PaymentLineId value) => value.Value.ToString("D");
}
