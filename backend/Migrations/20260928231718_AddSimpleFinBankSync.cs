using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddSimpleFinBankSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SimpleFinAccountId",
                table: "ImportDrafts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SimpleFinSyncedThrough",
                table: "ImportDrafts",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SimpleFinConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccessUrlEncrypted = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorsEncrypted = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimpleFinConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimpleFinConnections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SimpleFinAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ConnectionId = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameEncrypted = table.Column<string>(type: "text", nullable: false),
                    OrgNameEncrypted = table.Column<string>(type: "text", nullable: true),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: true),
                    LinkedAccountId = table.Column<int>(type: "integer", nullable: true),
                    SyncedThrough = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimpleFinAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SimpleFinAccounts_Accounts_LinkedAccountId",
                        column: x => x.LinkedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SimpleFinAccounts_SimpleFinConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "SimpleFinConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportDrafts_SimpleFinAccountId",
                table: "ImportDrafts",
                column: "SimpleFinAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinAccounts_ConnectionId_ExternalId",
                table: "SimpleFinAccounts",
                columns: new[] { "ConnectionId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinAccounts_LinkedAccountId",
                table: "SimpleFinAccounts",
                column: "LinkedAccountId",
                unique: true,
                filter: "\"LinkedAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SimpleFinConnections_UserId",
                table: "SimpleFinConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ImportDrafts_SimpleFinAccounts_SimpleFinAccountId",
                table: "ImportDrafts",
                column: "SimpleFinAccountId",
                principalTable: "SimpleFinAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImportDrafts_SimpleFinAccounts_SimpleFinAccountId",
                table: "ImportDrafts");

            migrationBuilder.DropTable(
                name: "SimpleFinAccounts");

            migrationBuilder.DropTable(
                name: "SimpleFinConnections");

            migrationBuilder.DropIndex(
                name: "IX_ImportDrafts_SimpleFinAccountId",
                table: "ImportDrafts");

            migrationBuilder.DropColumn(
                name: "SimpleFinAccountId",
                table: "ImportDrafts");

            migrationBuilder.DropColumn(
                name: "SimpleFinSyncedThrough",
                table: "ImportDrafts");
        }
    }
}
