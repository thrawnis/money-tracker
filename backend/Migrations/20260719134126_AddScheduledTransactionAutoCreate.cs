using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTransactionAutoCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScheduledTransactionId",
                table: "Transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoCreateFutureDays",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoCreateFutureTransactions",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_ScheduledTransactionId",
                table: "Transactions",
                column: "ScheduledTransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Transactions_ScheduledTransactions_ScheduledTransactionId",
                table: "Transactions",
                column: "ScheduledTransactionId",
                principalTable: "ScheduledTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Transactions_ScheduledTransactions_ScheduledTransactionId",
                table: "Transactions");

            migrationBuilder.DropIndex(
                name: "IX_Transactions_ScheduledTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "ScheduledTransactionId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "AutoCreateFutureDays",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AutoCreateFutureTransactions",
                table: "AspNetUsers");
        }
    }
}
