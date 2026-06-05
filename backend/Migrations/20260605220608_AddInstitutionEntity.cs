using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddInstitutionEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Institution",
                table: "Accounts");

            migrationBuilder.AddColumn<int>(
                name: "InstitutionId",
                table: "Accounts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Institutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Institutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_InstitutionId",
                table: "Accounts",
                column: "InstitutionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Institutions_InstitutionId",
                table: "Accounts",
                column: "InstitutionId",
                principalTable: "Institutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Institutions_InstitutionId",
                table: "Accounts");

            migrationBuilder.DropTable(
                name: "Institutions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_InstitutionId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InstitutionId",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "Institution",
                table: "Accounts",
                type: "text",
                nullable: true);
        }
    }
}
