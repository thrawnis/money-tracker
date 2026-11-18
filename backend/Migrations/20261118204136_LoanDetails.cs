using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class LoanDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoanChargeForTransactionId",
                table: "Transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoanChargeKind",
                table: "Transactions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoanDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    AnnualRate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "numeric", nullable: true),
                    TermMonths = table.Column<int>(type: "integer", nullable: true),
                    FirstPaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MonthlyPayment = table.Column<decimal>(type: "numeric", nullable: true),
                    EscrowAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    AutoCharges = table.Column<bool>(type: "boolean", nullable: false),
                    AutoChargesFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    InterestCategoryId = table.Column<int>(type: "integer", nullable: true),
                    EscrowCategoryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanDetails_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanDetails_Categories_EscrowCategoryId",
                        column: x => x.EscrowCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LoanDetails_Categories_InterestCategoryId",
                        column: x => x.InterestCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_LoanChargeForTransactionId",
                table: "Transactions",
                column: "LoanChargeForTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanDetails_AccountId",
                table: "LoanDetails",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanDetails_EscrowCategoryId",
                table: "LoanDetails",
                column: "EscrowCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanDetails_InterestCategoryId",
                table: "LoanDetails",
                column: "InterestCategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_Transactions_LoanChargeForTransactionId",
                table: "Transactions",
                column: "LoanChargeForTransactionId",
                principalTable: "Transactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_Transactions_LoanChargeForTransactionId",
                table: "Transactions");

            migrationBuilder.DropTable(
                name: "LoanDetails");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_LoanChargeForTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "LoanChargeForTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "LoanChargeKind",
                table: "Transactions");
        }
    }
}
