using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    public partial class AddTransferAccountId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransferAccountId",
                table: "Transactions",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransferAccountId",
                table: "Transactions");
        }
    }
}
