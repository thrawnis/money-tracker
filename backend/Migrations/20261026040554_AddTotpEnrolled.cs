using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddTotpEnrolled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TotpEnrolled",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Until passkeys, the authenticator app was the only second factor:
            // everyone enrolled in MFA has it.
            migrationBuilder.Sql("UPDATE \"AspNetUsers\" SET \"TotpEnrolled\" = \"MfaEnrolled\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotpEnrolled",
                table: "AspNetUsers");
        }
    }
}
