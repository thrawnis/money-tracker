using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUserAiServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiApiKeyEncrypted",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AiBaseUrl",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AiModel",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiApiKeyEncrypted",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiBaseUrl",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiModel",
                table: "AspNetUsers",
                type: "text",
                nullable: true);
        }
    }
}
