using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncAlertsAndWeeklyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LargeTxAlertsEnabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "LargeTxThreshold",
                table: "AspNetUsers",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 500m);

            migrationBuilder.AddColumn<bool>(
                name: "SyncAlertsEnabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WeeklyDigestDay",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyDigestEnabled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WeeklyDigestHour",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WeeklyDigestLastSentOn",
                table: "AspNetUsers",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LargeTxAlertsEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LargeTxThreshold",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SyncAlertsEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WeeklyDigestDay",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WeeklyDigestEnabled",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WeeklyDigestHour",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WeeklyDigestLastSentOn",
                table: "AspNetUsers");
        }
    }
}
