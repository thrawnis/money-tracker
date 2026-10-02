using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddBalanceOnlySyncAndValueHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DailyUpdateHour",
                table: "SimpleFinConnections",
                type: "integer",
                nullable: true);

            // Existing connections get the daily update at the default 8 pm,
            // same as new ones (the model's initializer only covers new rows).
            migrationBuilder.Sql("UPDATE \"SimpleFinConnections\" SET \"DailyUpdateHour\" = 20;");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAutoAttemptAt",
                table: "SimpleFinConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LastAutoUpdateDate",
                table: "SimpleFinConnections",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BalanceOnly",
                table: "SimpleFinAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AccountValueSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountValueSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountValueSnapshots_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HoldingSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    AccountId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    HoldingKeyEncrypted = table.Column<string>(type: "text", nullable: false),
                    SymbolEncrypted = table.Column<string>(type: "text", nullable: true),
                    DescriptionEncrypted = table.Column<string>(type: "text", nullable: true),
                    Shares = table.Column<decimal>(type: "numeric(24,8)", precision: 24, scale: 8, nullable: true),
                    MarketValue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CostBasis = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldingSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HoldingSnapshots_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountValueSnapshots_AccountId_Date",
                table: "AccountValueSnapshots",
                columns: new[] { "AccountId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountValueSnapshots_UserId",
                table: "AccountValueSnapshots",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HoldingSnapshots_AccountId_Date",
                table: "HoldingSnapshots",
                columns: new[] { "AccountId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountValueSnapshots");

            migrationBuilder.DropTable(
                name: "HoldingSnapshots");

            migrationBuilder.DropColumn(
                name: "DailyUpdateHour",
                table: "SimpleFinConnections");

            migrationBuilder.DropColumn(
                name: "LastAutoAttemptAt",
                table: "SimpleFinConnections");

            migrationBuilder.DropColumn(
                name: "LastAutoUpdateDate",
                table: "SimpleFinConnections");

            migrationBuilder.DropColumn(
                name: "BalanceOnly",
                table: "SimpleFinAccounts");
        }
    }
}
