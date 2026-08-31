using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SaleId = table.Column<string>(type: "TEXT", nullable: false),
                    ReceiptNumber = table.Column<string>(type: "TEXT", nullable: false),
                    ReceiptDocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReprintOf = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    RetailPrice = table.Column<long>(type: "INTEGER", nullable: false),
                    WholesalePrice = table.Column<long>(type: "INTEGER", nullable: true),
                    Cost = table.Column<long>(type: "INTEGER", nullable: true),
                    HasExpiry = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpirationDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SupportsPwdDiscount = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsSeniorDiscount = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeactivatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_ReceiptNumber",
                table: "PrintJobs",
                column: "ReceiptNumber");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_SaleId",
                table: "PrintJobs",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true);

            // BG-006 lane 5 caller-surface performance index (kept from the
            // pre-reset migrations; raw DDL — the EF model is unaware).
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_Products_Name_NOCASE" ON "Products" ("name" COLLATE NOCASE);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            // BG-P1 schema reset (orchestrate/bentago/inbox/BG-P1c-infrastructure.md,
            // docs/05 BG-P1). The store's data is disposable for this pivot: NO
            // data migration from the trip-era tables — the route-selling shape
            // (Trips, StockLoads, DailyCloses, TripStates, CellBreakdown, the
            // Movements trip/peddler kinds, Sales.trip_id/peddler_id) dies here.
            // One baseline creates the day-lifecycle schema in full:
            //  - Movement kinds FIXED per ADR 0004 §5 (GoodsReceived/FundIn/
            //    FundOut replace OpeningStock/Transfer/Trip kinds);
            //  - day documents DayOpens/DayCloses/DayReopens (float,
            //    expected/counted/difference/label) + GoodsReceipts + FundEntries
            //    as first-class document tables;
            //  - BIR readiness per ADR 0003: Sales.serial_number (long, unique,
            //    gapless via the ReceiptSerials counter; voids consume, no
            //    reuse), Sales.terminal_id, Sales.machine_serial (reserved);
            //  - append-only movements discipline (BEFORE UPDATE/DELETE triggers).
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
                    "observed_quantity" INTEGER NULL,
                    CHECK ("kind" IN ('GoodsReceived','SaleIssue','SaleIssueRevoke','ReturnToStock','Damage','Loss','AdjustmentIncrease','AdjustmentDecrease','Receipt','ReceiptRefund','ReceiptRevoke','FundIn','FundOut','CountObserved')),
                    CHECK ("doc_kind" IN ('Goods','Sale','Void','Return','Adjustment','Count','Fund')),
                    CHECK (("kind" = 'GoodsReceived' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssue' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'SaleIssueRevoke' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReturnToStock' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NOT NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NOT NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Damage' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Loss' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentIncrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'AdjustmentDecrease' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" > 0 AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'Receipt' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRefund' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'ReceiptRevoke' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NOT NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NOT NULL AND "reason" IS NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'FundIn' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'FundOut' AND "product_id" IS NULL AND "location_id" IS NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NOT NULL AND "amount_centavos" > 0 AND "reverses" IS NULL AND "reason" IS NOT NULL AND "observed_quantity" IS NULL)
                        OR ("kind" = 'CountObserved' AND "product_id" IS NOT NULL AND "location_id" IS NOT NULL AND "quantity" IS NULL AND "sale_line_id" IS NULL AND "payment_line_id" IS NULL AND "method" IS NULL AND "amount_centavos" IS NULL AND "reverses" IS NULL AND "reason" IS NULL AND "observed_quantity" >= 0))
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

                CREATE TABLE "Sales" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_Sales" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "location_id" TEXT NOT NULL,
                    "receipt_number" TEXT NOT NULL,
                    "serial_number" INTEGER NOT NULL,
                    "terminal_id" TEXT NOT NULL,
                    "machine_serial" TEXT NULL,
                    "created_utc" TEXT NOT NULL,
                    "created_by" TEXT NOT NULL,
                    "total_centavos" INTEGER NOT NULL);
                CREATE UNIQUE INDEX "IX_Sales_ReceiptNumber" ON "Sales" ("receipt_number");
                CREATE UNIQUE INDEX "IX_Sales_SerialNumber" ON "Sales" ("serial_number");
                CREATE INDEX "IX_Sales_DayKey" ON "Sales" ("day_key");

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
                CREATE INDEX "IX_PaymentLines_Reference" ON "PaymentLines" ("reference");

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

                CREATE TABLE "DayOpens" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_DayOpens" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "opening_float_centavos" INTEGER NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE INDEX "IX_DayOpens_DayKey" ON "DayOpens" ("day_key");

                CREATE TABLE "DayCloses" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_DayCloses" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "expected_cash_centavos" INTEGER NOT NULL,
                    "counted_cash_centavos" INTEGER NOT NULL,
                    "difference_centavos" INTEGER NOT NULL,
                    "label" TEXT NOT NULL,
                    "notes" TEXT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE INDEX "IX_DayCloses_DayKey" ON "DayCloses" ("day_key");

                CREATE TABLE "DayReopens" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_DayReopens" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "reason" TEXT NOT NULL,
                    "approved_by" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE INDEX "IX_DayReopens_DayKey" ON "DayReopens" ("day_key");

                CREATE TABLE "GoodsReceipts" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_GoodsReceipts" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE INDEX "IX_GoodsReceipts_DayKey" ON "GoodsReceipts" ("day_key");

                CREATE TABLE "GoodsReceiptLines" (
                    "goods_id" TEXT NOT NULL,
                    "product_id" TEXT NOT NULL,
                    "sku" TEXT NOT NULL,
                    "product_name" TEXT NOT NULL,
                    "quantity" INTEGER NOT NULL,
                    "unit_cost_centavos" INTEGER NOT NULL,
                    CONSTRAINT "PK_GoodsReceiptLines" PRIMARY KEY ("goods_id", "product_id"));

                CREATE TABLE "FundEntries" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_FundEntries" PRIMARY KEY,
                    "day_key" TEXT NOT NULL,
                    "direction" TEXT NOT NULL,
                    "method" TEXT NOT NULL,
                    "amount_centavos" INTEGER NOT NULL,
                    "reason" TEXT NOT NULL,
                    "approved_by" TEXT NULL,
                    "by" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL);
                CREATE INDEX "IX_FundEntries_DayKey" ON "FundEntries" ("day_key");

                CREATE TABLE "ReceiptSerials" (
                    "name" TEXT NOT NULL CONSTRAINT "PK_ReceiptSerials" PRIMARY KEY,
                    "next_value" INTEGER NOT NULL);

                INSERT INTO "ReceiptSerials" ("name", "next_value") VALUES ('sale_serial', 1);

                CREATE TABLE "AuditEvents" (
                    "sequence" INTEGER NOT NULL CONSTRAINT "PK_AuditEvents" PRIMARY KEY,
                    "actor_id" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "category" TEXT NOT NULL,
                    "message" TEXT NOT NULL,
                    "doc_kind" TEXT NULL,
                    "doc_id" TEXT NULL,
                    "terminal" TEXT NULL,
                    "reason" TEXT NULL);

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

                CREATE TABLE "IdempotencyKeys" (
                    "key" TEXT NOT NULL CONSTRAINT "PK_IdempotencyKeys" PRIMARY KEY,
                    "payload" TEXT NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "IdempotencyKeys";
                DROP TABLE IF EXISTS "ObservedQuantities";
                DROP TABLE IF EXISTS "FundBalances";
                DROP TABLE IF EXISTS "CellBalances";
                DROP TABLE IF EXISTS "LedgerHead";
                DROP TABLE IF EXISTS "AuditEvents";
                DROP TABLE IF EXISTS "ReceiptSerials";
                DROP TABLE IF EXISTS "FundEntries";
                DROP TABLE IF EXISTS "GoodsReceiptLines";
                DROP TABLE IF EXISTS "GoodsReceipts";
                DROP TABLE IF EXISTS "DayReopens";
                DROP TABLE IF EXISTS "DayCloses";
                DROP TABLE IF EXISTS "DayOpens";
                DROP TABLE IF EXISTS "CountLines";
                DROP TABLE IF EXISTS "PhysicalCounts";
                DROP TABLE IF EXISTS "Adjustments";
                DROP TABLE IF EXISTS "ReturnLines";
                DROP TABLE IF EXISTS "Returns";
                DROP TABLE IF EXISTS "SaleVoids";
                DROP TABLE IF EXISTS "PaymentLines";
                DROP TABLE IF EXISTS "SaleLines";
                DROP TABLE IF EXISTS "Sales";
                DROP TRIGGER IF EXISTS "Movements_NoDelete";
                DROP TRIGGER IF EXISTS "Movements_NoUpdate";
                DROP TABLE IF EXISTS "Movements";
                """);

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
