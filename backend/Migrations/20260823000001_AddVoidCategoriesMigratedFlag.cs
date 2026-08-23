using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddVoidCategoriesMigratedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VoidCategoriesMigrated",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoidCategoriesMigrated",
                table: "AspNetUsers");
        }
    }
}
