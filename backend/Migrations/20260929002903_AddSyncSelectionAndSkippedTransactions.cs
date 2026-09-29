using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncSelectionAndSkippedTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExcludedRowsJson",
                table: "ImportDrafts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimpleFinTxIdsJson",
                table: "ImportDrafts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SimpleFinSkippedTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SimpleFinAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PayeeEncrypted = table.Column<string>(type: "text", nullable: true),
                    SkippedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimpleFinSkippedTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimpleFinSkippedTransactions_SimpleFinAccounts_SimpleFinAcc~",
                        column: x => x.SimpleFinAccountId,
                        principalTable: "SimpleFinAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinSkippedTransactions_SimpleFinAccountId_ExternalId",
                table: "SimpleFinSkippedTransactions",
                columns: new[] { "SimpleFinAccountId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimpleFinSkippedTransactions");

            migrationBuilder.DropColumn(
                name: "ExcludedRowsJson",
                table: "ImportDrafts");

            migrationBuilder.DropColumn(
                name: "SimpleFinTxIdsJson",
                table: "ImportDrafts");
        }
    }
}
