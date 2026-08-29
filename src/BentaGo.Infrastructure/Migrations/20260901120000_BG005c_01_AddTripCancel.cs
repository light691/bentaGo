using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-005c trip-lifecycle schema: extend the Movements table with the
    /// 'TripCancel' movement kind (and its 'Cancel' document kind) so a trip
    /// cancellation is durable in the append-only stream. SQLite cannot ALTER
    /// a CHECK constraint, so the table is rebuilt in place: the append-only
    /// triggers are dropped, every existing row is copied verbatim into the
    /// new table, the old table is dropped/renamed, and the triggers are
    /// recreated. All under one transaction, so existing roots keep every
    /// ledger row, document row, and audit row (3a-style upgrade tests).
    /// The EF model is unchanged — Movements is not entity-mapped (raw SQL
    /// migrations own the ledger schema).
    /// </summary>
    public partial class BG005c_01_AddTripCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "Movements_NoUpdate";
                DROP TRIGGER IF EXISTS "Movements_NoDelete";

                CREATE TABLE "Movements_new" (
                    "sequence" INTEGER NOT NULL CONSTRAINT "PK_Movements" PRIMARY KEY,
                    "kind" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "actor_id" TEXT NOT NULL,
                    "doc_kind" TEXT NOT NULL,
                    "doc_id" TEXT NOT NULL,
                    "product_id" TEXT NULL,
                    "location_id" TEXT NULL,
                    "quantity" INTEGER NULL,
                    "sale_line_id" TEXT NULL,
                    "payment_line_id" TEXT NULL,
                    "method" TEXT NULL,
                    "amount_centavos" INTEGER NULL,
                    "reverses" INTEGER NULL,
                    "reason" TEXT NULL,
                    "trip_id" TEXT NULL,
                    "observed_quantity" INTEGER NULL,
                    CHECK ("kind" IN ('OpeningStock','TransferOut','TransferIn','SaleIssue','SaleIssueRevoke','ReturnToStock','Damage','Loss','AdjustmentIncrease','AdjustmentDecrease','Receipt','ReceiptRefund','ReceiptRevoke','CountObserved','TripClose','TripReopen','TripCancel')),
                    CHECK ("doc_kind" IN ('Load','Sale','Void','Return','Adjustment','Count','Close','Reopen','Transfer','Cancel')),
                    CHECK (("kind" = 'OpeningStock' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TransferOut' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TransferIn' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssue' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssueRevoke' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReturnToStock' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Damage' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Loss' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentIncrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentDecrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Receipt' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRefund' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRevoke' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'CountObserved' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" >= 0)
                        OR ("kind" = 'TripClose' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TripReopen' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TripCancel' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NOT NULL AND "observed_quantity" IS NULL))
                );

                INSERT INTO "Movements_new" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id",
                    "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method",
                    "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                SELECT "sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id",
                    "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method",
                    "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity"
                FROM "Movements";

                DROP TABLE "Movements";
                ALTER TABLE "Movements_new" RENAME TO "Movements";

                CREATE TRIGGER "Movements_NoUpdate"
                BEFORE UPDATE ON "Movements"
                BEGIN
                    SELECT RAISE(ABORT, 'movements_are_append_only');
                END;

                CREATE TRIGGER "Movements_NoDelete"
                BEFORE DELETE ON "Movements"
                BEGIN
                    SELECT RAISE(ABORT, 'movements_are_append_only');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "Movements_NoUpdate";
                DROP TRIGGER IF EXISTS "Movements_NoDelete";

                CREATE TABLE "Movements_new" (
                    "sequence" INTEGER NOT NULL CONSTRAINT "PK_Movements" PRIMARY KEY,
                    "kind" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "actor_id" TEXT NOT NULL,
                    "doc_kind" TEXT NOT NULL,
                    "doc_id" TEXT NOT NULL,
                    "product_id" TEXT NULL,
                    "location_id" TEXT NULL,
                    "quantity" INTEGER NULL,
                    "sale_line_id" TEXT NULL,
                    "payment_line_id" TEXT NULL,
                    "method" TEXT NULL,
                    "amount_centavos" INTEGER NULL,
                    "reverses" INTEGER NULL,
                    "reason" TEXT NULL,
                    "trip_id" TEXT NULL,
                    "observed_quantity" INTEGER NULL,
                    CHECK ("kind" IN ('OpeningStock','TransferOut','TransferIn','SaleIssue','SaleIssueRevoke','ReturnToStock','Damage','Loss','AdjustmentIncrease','AdjustmentDecrease','Receipt','ReceiptRefund','ReceiptRevoke','CountObserved','TripClose','TripReopen')),
                    CHECK ("doc_kind" IN ('Load','Sale','Void','Return','Adjustment','Count','Close','Reopen','Transfer')),
                    CHECK (("kind" = 'OpeningStock' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TransferOut' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TransferIn' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssue' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssueRevoke' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReturnToStock' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Damage' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Loss' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentIncrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentDecrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Receipt' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRefund' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRevoke' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'CountObserved' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NULL AND "observed_quantity" >= 0)
                        OR ("kind" = 'TripClose' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "trip_id" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'TripReopen' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "trip_id" IS NOT NULL AND "observed_quantity" IS NULL))
                );

                INSERT INTO "Movements_new" ("sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id",
                    "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method",
                    "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity")
                SELECT "sequence", "kind", "when_utc", "actor_id", "doc_kind", "doc_id",
                    "product_id", "location_id", "quantity", "sale_line_id", "payment_line_id", "method",
                    "amount_centavos", "reverses", "reason", "trip_id", "observed_quantity"
                FROM "Movements";

                DROP TABLE "Movements";
                ALTER TABLE "Movements_new" RENAME TO "Movements";

                CREATE TRIGGER "Movements_NoUpdate"
                BEFORE UPDATE ON "Movements"
                BEGIN
                    SELECT RAISE(ABORT, 'movements_are_append_only');
                END;

                CREATE TRIGGER "Movements_NoDelete"
                BEFORE DELETE ON "Movements"
                BEGIN
                    SELECT RAISE(ABORT, 'movements_are_append_only');
                END;
                """);
        }
    }
}
