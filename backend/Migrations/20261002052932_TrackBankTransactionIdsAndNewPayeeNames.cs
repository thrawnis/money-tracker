using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class TrackBankTransactionIdsAndNewPayeeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayeeNewNamesJson",
                table: "ImportDrafts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SimpleFinImportedTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SimpleFinAccountId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TransactionId = table.Column<int>(type: "integer", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimpleFinImportedTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimpleFinImportedTransactions_SimpleFinAccounts_SimpleFinAc~",
                        column: x => x.SimpleFinAccountId,
                        principalTable: "SimpleFinAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SimpleFinImportedTransactions_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinImportedTransactions_SimpleFinAccountId_ExternalId",
                table: "SimpleFinImportedTransactions",
                columns: new[] { "SimpleFinAccountId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinImportedTransactions_TransactionId",
                table: "SimpleFinImportedTransactions",
                column: "TransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimpleFinImportedTransactions");

            migrationBuilder.DropColumn(
                name: "PayeeNewNamesJson",
                table: "ImportDrafts");
        }
    }
}
