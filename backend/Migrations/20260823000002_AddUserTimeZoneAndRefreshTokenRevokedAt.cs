using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTimeZoneAndRefreshTokenRevokedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);

            // Existing revoked tokens predate this column. Backdating them to
            // their creation time keeps the reuse grace window in
            // AuthController.Refresh from treating a long-dead token as
            // "just rotated" the first time it is replayed after deploy.
            migrationBuilder.Sql(@"UPDATE ""RefreshTokens"" SET ""RevokedAt"" = ""CreatedAt"" WHERE ""IsRevoked"" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                table: "RefreshTokens");
        }
    }
}
