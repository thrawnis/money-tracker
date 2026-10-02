using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRateLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastManualSyncAt",
                table: "SimpleFinConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestCount",
                table: "SimpleFinConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RequestDay",
                table: "SimpleFinConnections",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastManualSyncAt",
                table: "SimpleFinConnections");

            migrationBuilder.DropColumn(
                name: "RequestCount",
                table: "SimpleFinConnections");

            migrationBuilder.DropColumn(
                name: "RequestDay",
                table: "SimpleFinConnections");
        }
    }
}
