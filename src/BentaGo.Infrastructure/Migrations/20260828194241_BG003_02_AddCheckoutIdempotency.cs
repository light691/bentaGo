using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-003 migration 02 — the idempotency slice (D1): the generic key =>
    /// payload mapping table, inserted in the SAME transaction as the sale it
    /// idempotently carries. The Key is the PK, so a duplicate submit violates
    /// the unique key inside its transaction and is replayed, never an error.
    /// This is the migration that separates "migration 01" (a real ledger
    /// schema with seeded movements) from the head schema, so lane 9's
    /// prior-schema fixture is reproducible and the upgrade preserves rows.
    /// </summary>
    public partial class BG003_02_AddCheckoutIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
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
                """);
        }
    }
}
