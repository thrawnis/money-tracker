using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class DailyUserBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupExcludedSections",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupKeyEncrypted",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupLastError",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastDailyBackupOn",
                table: "AspNetUsers",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupExcludedSections",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "BackupKeyEncrypted",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "BackupLastError",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LastDailyBackupOn",
                table: "AspNetUsers");
        }
    }
}
