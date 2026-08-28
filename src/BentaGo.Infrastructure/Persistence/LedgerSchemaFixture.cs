using System.Globalization;
using System.IO;
using System.Text;
using BentaGo.Application.Contracts;
using BentaGo.Application.Scenario;
using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Builds the lane-9 "prior schema" fixture: a real database at migration
/// BG003_01 (ledger schema, BEFORE the idempotency migration) with a
/// deterministic seeded mini-day — a trip, an opening load, a load out, and
/// one cash sale — so upgrades and restores can prove row preservation and
/// identical reports. Code-built (no checked-in binary), deterministic ids
/// and clock, so re-runs are byte-identical (docs/05 lane 9, BG-003 §12 U6).
/// </summary>
public static class LedgerSchemaFixture
{
    public const string Migration01Id = "20260828194050_BG003_01_CreateLedgerSchema";

    /// <summary>Fixed UTC clock; the whole seeded day happens in it.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds (or rebuilds) the fixture under <paramref name="dataRoot"/>:
    /// migrates to BG003_01 then seeds movements, documents, audit events,
    /// ledger head, and projections. Returns the database path.
    /// </summary>
    public static async Task<string> BuildAsync(string dataRoot)
    {
        var paths = new DataPathProvider(dataRoot);
        paths.EnsureCreated();

        string dbPath = Path.Combine(paths.DataDir, "bentago.db");
        var factory = new BentaGoDbContextFactory($"Data Source={dbPath};Pooling=False");
        IDbContextFactory<BentaGoDbContext> contextFactory = factory;
        await using var context = await contextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync(Migration01Id);

        Seed(dbPath);
        return dbPath;
    }

    /// <summary>
    /// Deterministic render of whatever the store holds (movements, audits,
    /// balances) — lane 10 compares the pre-upgrade fixture render with the
    /// render after restore, so the reports must match byte-for-byte.
    /// </summary>
    public static ScenarioReport Render(ILedgerStore store)
    {
        var text = new StringBuilder();
        text.AppendLine("BentaGo fixture: migration-01 mini-day");
        text.AppendLine("=======================================");
        foreach (Movement movement in store.Movements)
        {
            text.AppendLine($"{movement.Sequence,4} {movement.Kind,-18} {Describe(movement)}");
        }

        foreach (AuditEvent audit in store.Audits)
        {
            text.AppendLine($"  [audit] {audit.Message}");
        }

        text.AppendLine();
        text.AppendLine("BALANCES");
        text.AppendLine("--------");
        foreach (Fund fund in Enum.GetValues<Fund>())
        {
            text.AppendLine($"  {fund,-14} {Fmt(store.State.FundsOnHand(fund))}");
        }

        var cells = new HashSet<string>();
        foreach (Movement movement in store.Movements)
        {
            switch (movement)
            {
                case OpeningStock s:
                    cells.Add($"{s.ProductId.Value:N}|{s.LocationId.Value:N}");
                    break;
                case TransferOut t:
                    cells.Add($"{t.ProductId.Value:N}|{t.LocationId.Value:N}");
                    break;
                case TransferIn t:
                    cells.Add($"{t.ProductId.Value:N}|{t.LocationId.Value:N}");
                    break;
                case SaleIssue s:
                    cells.Add($"{s.ProductId.Value:N}|{s.LocationId.Value:N}");
                    break;
                case SaleIssueRevoke s:
                    cells.Add($"{s.ProductId.Value:N}|{s.LocationId.Value:N}");
                    break;
                case ReturnToStock r:
                    cells.Add($"{r.ProductId.Value:N}|{r.LocationId.Value:N}");
                    break;
                case Damage d:
                    cells.Add($"{d.ProductId.Value:N}|{d.LocationId.Value:N}");
                    break;
                case Loss l:
                    cells.Add($"{l.ProductId.Value:N}|{l.LocationId.Value:N}");
                    break;
                case AdjustmentIncrease a:
                    cells.Add($"{a.ProductId.Value:N}|{a.LocationId.Value:N}");
                    break;
                case AdjustmentDecrease a:
                    cells.Add($"{a.ProductId.Value:N}|{a.LocationId.Value:N}");
                    break;
                case CountObserved c:
                    cells.Add($"{c.ProductId.Value:N}|{c.LocationId.Value:N}");
                    break;
            }
        }

        foreach (string cell in cells.OrderBy(c => c, StringComparer.Ordinal))
        {
            string[] parts = cell.Split('|');
            var product = new ProductId(Guid.Parse(parts[0]));
            var location = new LocationId(Guid.Parse(parts[1]));
            text.AppendLine($"  {parts[0]} @ {parts[1]} = {store.State.StockOnHand(product, location).Units}");
        }

        return new ScenarioReport(text.ToString());
    }

    private static string Fmt(Money money) =>
        money.ToPesos().ToString("0.00", CultureInfo.InvariantCulture);

    private static string Describe(Movement m) =>
        m switch
        {
            OpeningStock s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} +{s.Quantity.Units}",
            TransferOut t => $"{t.ProductId.Value:N} @ {t.LocationId.Value:N} -{t.Quantity.Units}",
            TransferIn t => $"{t.ProductId.Value:N} @ {t.LocationId.Value:N} +{t.Quantity.Units}",
            SaleIssue s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} -{s.Quantity.Units}",
            SaleIssueRevoke s => $"{s.ProductId.Value:N} @ {s.LocationId.Value:N} +{s.Quantity.Units} (reverses {s.Reverses})",
            ReturnToStock r => $"{r.ProductId.Value:N} @ {r.LocationId.Value:N} +{r.Quantity.Units} (reverses {r.Reverses})",
            Receipt r => $"{r.Method} +{Fmt(r.Amount)}",
            ReceiptRefund r => $"{r.Method} -{Fmt(r.Amount)} (reverses {r.Reverses})",
            ReceiptRevoke r => $"{r.Method} -{Fmt(r.Amount)} (reverses {r.Reverses})",
            CountObserved c => $"{c.ProductId.Value:N} @ {c.LocationId.Value:N} observed {c.ObservedQuantity.Units}",
            Damage d => $"{d.ProductId.Value:N} @ {d.LocationId.Value:N} -{d.Quantity.Units} ({d.Reason})",
            Loss l => $"{l.ProductId.Value:N} @ {l.LocationId.Value:N} -{l.Quantity.Units} ({l.Reason})",
            AdjustmentIncrease a => $"{a.ProductId.Value:N} @ {a.LocationId.Value:N} +{a.Quantity.Units} ({a.Reason})",
            AdjustmentDecrease a => $"{a.ProductId.Value:N} @ {a.LocationId.Value:N} -{a.Quantity.Units} ({a.Reason})",
            TripClose c => $"trip {c.TripId.Value:N}",
            TripReopen c => $"trip {c.TripId.Value:N} ({c.Reason})",
            _ => m.Kind.ToString(),
        };

    private static void Seed(string dbPath)
    {
        var ids = new ScenarioIdSource("fixture-v1");
        string trip = Id(ids.Next("trip-1"));
        string peddler = Id(ids.Next("peddler-1"));
        string route = Id(ids.Next("loc-route"));
        string store = Id(ids.Next("loc-store"));
        string user = Id(ids.Next("user"));
        string sardines = Id(ids.Next("product-sardines"));
        string openLoad = Id(ids.Next("load-open"));
        string load = Id(ids.Next("load-1"));
        string sale = Id(ids.Next("sale-1"));
        string line = Id(ids.Next("line-1"));
        string payment = Id(ids.Next("pay-1"));
        string when0 = IsoUtc(T0);
        string whenSale = IsoUtc(T0.AddMinutes(30));

        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO "Trips" ("id", "peddler_id", "location_id", "created_utc", "created_by")
                VALUES ($trip, $peddler, $route, $when0, $user);
                INSERT INTO "StockLoads" ("id", "load_number", "trip_id", "by", "when_utc")
                VALUES ($openLoad, 'OPEN-1', $zero, $user, $when0);
                INSERT INTO "StockLoads" ("id", "load_number", "trip_id", "by", "when_utc")
                VALUES ($load, 'LD-1', $trip, $user, $when0);
                INSERT INTO "StockLoadLines" ("load_id", "product_id", "sku", "product_name", "quantity", "unit_cost_centavos")
                VALUES ($load, $sardines, 'SARD', 'Sardines', 20, 3000);
                INSERT INTO "Sales" ("id", "trip_id", "peddler_id", "location_id", "receipt_number", "created_utc", "created_by", "total_centavos")
                VALUES ($sale, $trip, $peddler, $route, 'R-000001', $whenSale, $user, 22500);
                INSERT INTO "SaleLines" ("id", "sale_id", "product_id", "sku", "product_name", "unit_price_centavos", "quantity", "discount_centavos")
                VALUES ($line, $sale, $sardines, 'SARD', 'Sardines', 4500, 5, 0);
                INSERT INTO "PaymentLines" ("id", "sale_id", "method", "reference", "amount_centavos", "tendered_centavos", "change_centavos")
                VALUES ($payment, $sale, 'Cash', '', 22500, 22500, 0);

                INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id", "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method", "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                VALUES (1, 'OpeningStock', $when0, $user, 'Load', $openLoad, $sardines, $store, 40, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id", "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method", "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                VALUES (2, 'TransferOut', $when0, $user, 'Load', $load, $sardines, $store, 20, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id", "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method", "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                VALUES (3, 'TransferIn', $when0, $user, 'Load', $load, $sardines, $route, 20, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id", "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method", "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                VALUES (4, 'SaleIssue', $whenSale, $user, 'Sale', $sale, $sardines, $route, 5, $line, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                INSERT INTO "Movements" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id", "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method", "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                VALUES (5, 'Receipt', $whenSale, $user, 'Sale', $sale, NULL, NULL, NULL, NULL, $payment, 'Cash', 22500, NULL, NULL, NULL, NULL);

                INSERT INTO "AuditEvents" ("sequence", "actor_id", "when_utc", "category", "message", "doc_kind", "doc_id")
                VALUES (-1, $user, $when0, 'FIXTURE', 'seeded fixture day', NULL, NULL);

                INSERT INTO "LedgerHead" ("id", "last_sequence", "documents_version") VALUES (1, 5, 1);

                INSERT INTO "CellBalances" ("product_id", "location_id", "units") VALUES ($sardines, $store, 20);
                INSERT INTO "CellBalances" ("product_id", "location_id", "units") VALUES ($sardines, $route, 15);
                INSERT INTO "FundBalances" ("fund", "centavos") VALUES ('PhysicalCash', 22500);
                INSERT INTO "TripStates" ("trip_id", "status", "opened_utc", "closed_utc") VALUES ($trip, 'Open', $when0, NULL);
                INSERT INTO "CellBreakdown" ("product_id", "location_id", "starting", "loaded", "sold", "returned", "damaged", "lost", "adjusted", "ending", "observed", "variance")
                VALUES ($sardines, $store, 20, 0, 0, 0, 0, 0, 0, 20, NULL, NULL);
                INSERT INTO "CellBreakdown" ("product_id", "location_id", "starting", "loaded", "sold", "returned", "damaged", "lost", "adjusted", "ending", "observed", "variance")
                VALUES ($sardines, $route, 0, 20, 5, 0, 0, 0, 0, 15, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$trip", trip);
            command.Parameters.AddWithValue("$peddler", peddler);
            command.Parameters.AddWithValue("$route", route);
            command.Parameters.AddWithValue("$store", store);
            command.Parameters.AddWithValue("$user", user);
            command.Parameters.AddWithValue("$sardines", sardines);
            command.Parameters.AddWithValue("$openLoad", openLoad);
            command.Parameters.AddWithValue("$load", load);
            command.Parameters.AddWithValue("$sale", sale);
            command.Parameters.AddWithValue("$line", line);
            command.Parameters.AddWithValue("$payment", payment);
            command.Parameters.AddWithValue("$when0", when0);
            command.Parameters.AddWithValue("$whenSale", whenSale);
            command.Parameters.AddWithValue("$zero", Guid.Empty.ToString("D"));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        connection.Close();
    }

    private static string Id(Guid value) => value.ToString("D");

    private static string IsoUtc(DateTimeOffset when) =>
        when.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
