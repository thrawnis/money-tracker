using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    public partial class AddScheduledTransferAccount : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransferAccountId",
                table: "ScheduledTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTransactions_TransferAccountId",
                table: "ScheduledTransactions",
                column: "TransferAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduledTransactions_Accounts_TransferAccountId",
                table: "ScheduledTransactions",
                column: "TransferAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledTransactions_Accounts_TransferAccountId",
                table: "ScheduledTransactions");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTransactions_TransferAccountId",
                table: "ScheduledTransactions");

            migrationBuilder.DropColumn(
                name: "TransferAccountId",
                table: "ScheduledTransactions");
        }
    }
}
