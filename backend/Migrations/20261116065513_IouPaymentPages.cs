using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class IouPaymentPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailEncrypted",
                table: "IouPeople",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodsEncrypted",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IouPaidNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouPaidNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouPaidNotices_IouPeople_PersonId",
                        column: x => x.PersonId,
                        principalTable: "IouPeople",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IouShareLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouShareLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouShareLinks_IouPeople_PersonId",
                        column: x => x.PersonId,
                        principalTable: "IouPeople",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IouPaidNotices_PersonId",
                table: "IouPaidNotices",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_IouPaidNotices_UserId",
                table: "IouPaidNotices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IouShareLinks_PersonId",
                table: "IouShareLinks",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_IouShareLinks_TokenHash",
                table: "IouShareLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IouShareLinks_UserId_PersonId",
                table: "IouShareLinks",
                columns: new[] { "UserId", "PersonId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IouPaidNotices");

            migrationBuilder.DropTable(
                name: "IouShareLinks");

            migrationBuilder.DropColumn(
                name: "EmailEncrypted",
                table: "IouPeople");

            migrationBuilder.DropColumn(
                name: "PaymentMethodsEncrypted",
                table: "AspNetUsers");
        }
    }
}
