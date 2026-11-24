using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class RefreshTokenFamilies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FamilyId",
                table: "RefreshTokens",
                type: "character varying(36)",
                maxLength: 36,
                nullable: false,
                defaultValue: "");

            // Existing sessions: each token becomes its own family, so a replay of
            // an old token can't sign out any other device. Tokens issued from now
            // on carry their sign-in's family forward on every refresh.
            migrationBuilder.Sql("UPDATE \"RefreshTokens\" SET \"FamilyId\" = 'legacy-' || \"Id\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "RefreshTokens");
        }
    }
}
