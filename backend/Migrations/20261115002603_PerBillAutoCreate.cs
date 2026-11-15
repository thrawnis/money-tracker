using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class PerBillAutoCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCreate",
                table: "ScheduledTransactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoCreateDaysAhead",
                table: "ScheduledTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 31);

            // Auto-create moved from a per-user setting to a per-bill one: bills
            // of users who had it on keep pre-creating, with the same window
            // (null meant the built-in 31 days). Everyone else's stay off.
            migrationBuilder.Sql("""
                UPDATE "ScheduledTransactions" s
                SET "AutoCreate" = TRUE,
                    "AutoCreateDaysAhead" = COALESCE(u."AutoCreateFutureDays", 31)
                FROM "AspNetUsers" u
                WHERE s."UserId" = u."Id" AND u."AutoCreateFutureTransactions";
                """);

            migrationBuilder.DropColumn(
                name: "AutoCreateFutureDays",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AutoCreateFutureTransactions",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCreate",
                table: "ScheduledTransactions");

            migrationBuilder.DropColumn(
                name: "AutoCreateDaysAhead",
                table: "ScheduledTransactions");

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
        }
    }
}
