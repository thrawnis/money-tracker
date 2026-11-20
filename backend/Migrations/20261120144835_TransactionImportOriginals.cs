using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class TransactionImportOriginals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions");

            migrationBuilder.AddColumn<string>(
                name: "ImportExternalId",
                table: "Transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportSourceEncrypted",
                table: "Transactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ImportedAmount",
                table: "Transactions",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImportedAt",
                table: "Transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ImportedDate",
                table: "Transactions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedMemoEncrypted",
                table: "Transactions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportedPayeeEncrypted",
                table: "Transactions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId_ImportExternalId",
                table: "Transactions",
                columns: new[] { "AccountId", "ImportExternalId" });

            // Transactions bank sync already brought in: their SimpleFIN id is
            // known, so later imports can match them by it.
            migrationBuilder.Sql(@"
                UPDATE ""Transactions"" t SET ""ImportExternalId"" = i.""ExternalId""
                FROM ""SimpleFinImportedTransactions"" i
                WHERE i.""TransactionId"" = t.""Id"" AND t.""ImportExternalId"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Transactions_AccountId_ImportExternalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportExternalId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportSourceEncrypted",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportedAmount",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportedAt",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportedDate",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportedMemoEncrypted",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ImportedPayeeEncrypted",
                table: "Transactions");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountId",
                table: "Transactions",
                column: "AccountId");
        }
    }
}
