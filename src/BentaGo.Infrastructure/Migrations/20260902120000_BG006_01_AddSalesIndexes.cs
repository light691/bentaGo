using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-006 performance indexes: the caller-surface queries are folds or EF
    /// lookups whose hot paths touch three columns that had no index:
    /// Products.Name (infix search, NOCASE so the FIND-anything search and
    /// EF.LIKE agree on collation), Sales.trip_id+created_utc (history
    /// bucketing: per-trip, per-day), PaymentLines.reference (receipt look-up
    /// by GCash/Maya/bank reference). Index-only: no table rebuild, no data
    /// copy, no trigger churn; every existing row keeps every value. The EF
    /// model is unchanged (the snapshot is untouched — the Products index is
    /// declared by raw SQL on purpose so the EF model stays byte-identical).
    /// </summary>
    public partial class BG006_01_AddSalesIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Products_Name_NOCASE"
                    ON "Products" ("Name" COLLATE NOCASE);

                CREATE INDEX IF NOT EXISTS "IX_Sales_TripCreated"
                    ON "Sales" ("trip_id", "created_utc");

                CREATE INDEX IF NOT EXISTS "IX_PaymentLines_Reference"
                    ON "PaymentLines" ("reference");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_PaymentLines_Reference";
                DROP INDEX IF EXISTS "IX_Sales_TripCreated";
                DROP INDEX IF EXISTS "IX_Products_Name_NOCASE";
                """);
        }
    }
}
