using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class FlexibleRecurrenceFrequency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Frequency",
                table: "ScheduledTransactions",
                newName: "FrequencyUnit");

            migrationBuilder.AddColumn<int>(
                name: "FrequencyInterval",
                table: "ScheduledTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrequencyInterval",
                table: "ScheduledTransactions");

            migrationBuilder.RenameColumn(
                name: "FrequencyUnit",
                table: "ScheduledTransactions",
                newName: "Frequency");
        }
    }
}
