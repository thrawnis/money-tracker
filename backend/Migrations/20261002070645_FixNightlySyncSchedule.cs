using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class FixNightlySyncSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The nightly sync is no longer user-editable: every connection
            // runs at 8 pm Pacific, including any that had been turned off.
            migrationBuilder.Sql("UPDATE \"SimpleFinConnections\" SET \"DailyUpdateHour\" = 20;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
