using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BentaGo.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// BG-005a identity + audit schema: Users and Peddlers tables (EF-shaped:
    /// branded ids and Role as TEXT, auth state columns on Users, unique
    /// username) plus AuditEvents hardening — Terminal/Reason columns and the
    /// identity PRIMARY KEY on Sequence. The hardened AuditEvents table is a
    /// full rebuild (SQLite cannot add a PK to an existing table): rows are
    /// copied in rowid order and receive distinct auto-assigned identities
    /// (the -1 placeholder from the ledger path is replaced by the DB rowid;
    /// record counts and rendering are unchanged). Migration is additive —
    /// existing roots keep every ledger row and their AppSettings seed rows.
    /// </summary>
    public partial class BG005_01_AddIdentityAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Peddlers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Peddlers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    PeddlerLink = table.Column<string>(type: "TEXT", nullable: true),
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
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            // AuditEvents hardening (BG-005a): identity PK on Sequence + the
            // Terminal/Reason fields. SQLite cannot ALTER a PK, so the table
            // is rebuilt; existing rows are copied in rowid order and get
            // distinct auto-assigned identities (the -1 placeholder was never
            // a real identity). RecordAudit now assigns/persists the sequence.
            migrationBuilder.Sql(
                """
                CREATE TABLE "AuditEvents_new" (
                    "sequence" INTEGER NOT NULL CONSTRAINT "PK_AuditEvents" PRIMARY KEY,
                    "actor_id" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "category" TEXT NOT NULL,
                    "message" TEXT NOT NULL,
                    "doc_kind" TEXT NULL,
                    "doc_id" TEXT NULL,
                    "terminal" TEXT NULL,
                    "reason" TEXT NULL);

                INSERT INTO "AuditEvents_new" ("actor_id", "when_utc", "category", "message", "doc_kind", "doc_id", "terminal", "reason")
                SELECT "actor_id", "when_utc", "category", "message", "doc_kind", "doc_id", NULL, NULL
                FROM "AuditEvents" ORDER BY "rowid";

                DROP TABLE "AuditEvents";
                ALTER TABLE "AuditEvents_new" RENAME TO "AuditEvents";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "AuditEvents_old" (
                    "sequence" INTEGER NOT NULL,
                    "actor_id" TEXT NOT NULL,
                    "when_utc" TEXT NOT NULL,
                    "category" TEXT NOT NULL,
                    "message" TEXT NOT NULL,
                    "doc_kind" TEXT NULL,
                    "doc_id" TEXT NULL);

                INSERT INTO "AuditEvents_old" ("sequence", "actor_id", "when_utc", "category", "message", "doc_kind", "doc_id")
                SELECT "sequence", "actor_id", "when_utc", "category", "message", "doc_kind", "doc_id"
                FROM "AuditEvents" ORDER BY "rowid";

                DROP TABLE "AuditEvents";
                ALTER TABLE "AuditEvents_old" RENAME TO "AuditEvents";
                """);

            migrationBuilder.DropTable(
                name: "Peddlers");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
