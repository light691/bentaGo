using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-005b catalog schema: the Products table for the additive v2 product
    /// contract. SKU is the unique stable business key (derived in the
    /// client-mapping layer, never a file column); Barcode is optional and
    /// unique when present (barcode2 stays dormant — not stored). Money is
    /// integer centavos; stock_qty is deliberately absent (inventory enters
    /// via movements in BG-005c). Migration is additive — existing roots keep
    /// every ledger row, audit row, and seed row.
    /// </summary>
    public partial class BG005b_01_AddProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
