using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-003 ledger schema: one Movements table (kind discriminator +
    /// normalized payload columns pinned by a per-kind CHECK matrix), the
    /// document tables, the five durable projection tables, AuditEvents, and
    /// the LedgerHead watermark. Append-only is enforced by BEFORE UPDATE /
    /// BEFORE DELETE triggers; every unique business reference gets its own
    /// unique index. The split (BG003_02 adds idempotency) makes "migration
    /// 01" a real prior schema with a ledger that lane 9 seeds and preserves.
    /// </summary>
    public partial class BG003_01_CreateLedgerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "Movements" (
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

                CREATE TABLE "Trips" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_Trips" PRIMARY KEY,
                    "peddler_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "created_utc" TEXT NOT NULL,
                    "created_by" TEXT NOT NULL);

                CREATE TABLE "Sales" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_Sales" PRIMARY KEY,
                    "trip_id" TEXT NOT NULL,
                    "peddler_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "receipt_number" TEXT NOT NULL,
                    "created_utc" TEXT NOT NULL,
                    "created_by" TEXT NOT NULL,
                    "total_centavos" INTEGER NOT NULL);
                CREATE UNIQUE INDEX "IX_Sales_ReceiptNumber" ON "Sales" ("receipt_number");

                CREATE TABLE "SaleLines" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_SaleLines" PRIMARY KEY,
                    "sale_id" TEXT NOT NULL,
                    "product_id" TEXT NOT NULL,
                    "sku" TEXT NOT NULL,
                    "product_name" TEXT NOT NULL,
                    "unit_price_centavos" INTEGER NOT NULL,
                    "quantity" INTEGER NOT NULL,
                    "discount_centavos" INTEGER NOT NULL);

                CREATE TABLE "PaymentLines" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_PaymentLines" PRIMARY KEY,
                    "sale_id" TEXT NOT NULL,
                    "method" TEXT NOT NULL,
                    "reference" TEXT NOT NULL,
                    "amount_centavos" INTEGER NOT NULL,
                    "tendered_centavos" INTEGER NOT NULL,
                    "change_centavos" INTEGER NOT NULL);

                CREATE TABLE "SaleVoids" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_SaleVoids" PRIMARY KEY,
                    "original_sale_id" TEXT NOT NULL,
                    "reason" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);

                CREATE TABLE "Returns" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_Returns" PRIMARY KEY,
                    "original_sale_id" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);

                CREATE TABLE "ReturnLines" (
                    "return_id" TEXT NOT NULL,
                    "original_line" TEXT NOT NULL,
                    "quantity" INTEGER NOT NULL,
                    "reason" TEXT NOT NULL,
                    CONSTRAINT "PK_ReturnLines" PRIMARY KEY ("return_id", "original_line"));

                CREATE TABLE "StockLoads" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_StockLoads" PRIMARY KEY,
                    "load_number" TEXT NOT NULL,
                    "trip_id" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE UNIQUE INDEX "IX_StockLoads_LoadNumber" ON "StockLoads" ("load_number");

                CREATE TABLE "StockLoadLines" (
                    "load_id" TEXT NOT NULL,
                    "product_id" TEXT NOT NULL,
                    "sku" TEXT NOT NULL,
                    "product_name" TEXT NOT NULL,
                    "quantity" INTEGER NOT NULL,
                    "unit_cost_centavos" INTEGER NOT NULL);

                CREATE TABLE "Adjustments" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_Adjustments" PRIMARY KEY,
                    "product_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "reason" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);

                CREATE TABLE "PhysicalCounts" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_PhysicalCounts" PRIMARY KEY,
                    "location_id" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);

                CREATE TABLE "CountLines" (
                    "count_id" TEXT NOT NULL,
                    "product_id" TEXT NOT NULL,
                    "observed" INTEGER NOT NULL,
                    "notes" TEXT NULL);

                CREATE TABLE "DailyCloses" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_DailyCloses" PRIMARY KEY,
                    "trip_id" TEXT NOT NULL,
                    "expected_cash_centavos" INTEGER NOT NULL,
                    "counted_cash_centavos" INTEGER NOT NULL,
                    "variance_centavos" INTEGER NOT NULL,
                    "notes" TEXT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);

                CREATE TABLE "AuditEvents" (
                    "sequence" INTEGER NOT NULL CONSTRAINT "PK_AuditEvents" PRIMARY KEY,
                    "actor_id" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "category" TEXT NOT NULL,
                    "message" TEXT NOT NULL,
                    "doc_kind" TEXT NULL,
                    "doc_id" TEXT NULL);

                CREATE TABLE "LedgerHead" (
                    "id" INTEGER NOT NULL CONSTRAINT "PK_LedgerHead" PRIMARY KEY,
                    "last_sequence" INTEGER NOT NULL,
                    "documents_version" INTEGER NOT NULL);

                CREATE TABLE "CellBalances" (
                    "product_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "units" INTEGER NOT NULL,
                    CONSTRAINT "PK_CellBalances" PRIMARY KEY ("product_id", "location_id"));

                CREATE TABLE "FundBalances" (
                    "fund" TEXT NOT NULL CONSTRAINT "PK_FundBalances" PRIMARY KEY,
                    "centavos" INTEGER NOT NULL);

                CREATE TABLE "ObservedQuantities" (
                    "product_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "units" INTEGER NOT NULL,
                    CONSTRAINT "PK_ObservedQuantities" PRIMARY KEY ("product_id", "location_id"));

                CREATE TABLE "TripStates" (
                    "trip_id" TEXT NOT NULL CONSTRAINT "PK_TripStates" PRIMARY KEY,
                    "status" TEXT NOT NULL,
                    "opened_utc" TEXT NULL,
                    "closed_utc" TEXT NULL);

                CREATE TABLE "CellBreakdown" (
                    "product_id" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "starting" INTEGER NOT NULL,
                    "loaded" INTEGER NOT NULL,
                    "sold" INTEGER NOT NULL,
                    "returned" INTEGER NOT NULL,
                    "damaged" INTEGER NOT NULL,
                    "lost" INTEGER NOT NULL,
                    "adjusted" INTEGER NOT NULL,
                    "ending" INTEGER NOT NULL,
                    "observed" INTEGER NULL,
                    "variance" INTEGER NULL,
                    CONSTRAINT "PK_CellBreakdown" PRIMARY KEY ("product_id", "location_id"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "CellBreakdown";
                DROP TABLE IF EXISTS "TripStates";
                DROP TABLE IF EXISTS "ObservedQuantities";
                DROP TABLE IF EXISTS "FundBalances";
                DROP TABLE IF EXISTS "CellBalances";
                DROP TABLE IF EXISTS "LedgerHead";
                DROP TABLE IF EXISTS "AuditEvents";
                DROP TABLE IF EXISTS "DailyCloses";
                DROP TABLE IF EXISTS "CountLines";
                DROP TABLE IF EXISTS "PhysicalCounts";
                DROP TABLE IF EXISTS "Adjustments";
                DROP TABLE IF EXISTS "StockLoadLines";
                DROP TABLE IF EXISTS "StockLoads";
                DROP TABLE IF EXISTS "ReturnLines";
                DROP TABLE IF EXISTS "Returns";
                DROP TABLE IF EXISTS "SaleVoids";
                DROP TABLE IF EXISTS "PaymentLines";
                DROP TABLE IF EXISTS "SaleLines";
                DROP TABLE IF EXISTS "Sales";
                DROP TABLE IF EXISTS "Trips";
                DROP TRIGGER IF EXISTS "Movements_NoDelete";
                DROP TRIGGER IF EXISTS "Movements_NoUpdate";
                DROP TABLE IF EXISTS "Movements";
                """);
        }
    }
}
