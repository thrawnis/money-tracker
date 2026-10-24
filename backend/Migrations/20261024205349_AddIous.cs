using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddIous : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IouPeople",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    NameEncrypted = table.Column<string>(type: "text", nullable: false),
                    NotesEncrypted = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouPeople", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouPeople_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IouEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DescriptionEncrypted = table.Column<string>(type: "text", nullable: true),
                    NotesEncrypted = table.Column<string>(type: "text", nullable: true),
                    TransactionId = table.Column<int>(type: "integer", nullable: true),
                    PaidOffDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidOffManually = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouEntries_IouPeople_PersonId",
                        column: x => x.PersonId,
                        principalTable: "IouPeople",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IouEntries_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "IouPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Direction = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    NotesEncrypted = table.Column<string>(type: "text", nullable: true),
                    TransactionId = table.Column<int>(type: "integer", nullable: true),
                    AppliesToEntryId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouPayments_IouEntries_AppliesToEntryId",
                        column: x => x.AppliesToEntryId,
                        principalTable: "IouEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_IouPayments_IouPeople_PersonId",
                        column: x => x.PersonId,
                        principalTable: "IouPeople",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IouPayments_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "IouAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    EntryId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IouAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IouAllocations_IouEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "IouEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IouAllocations_IouPayments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "IouPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IouAllocations_EntryId",
                table: "IouAllocations",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_IouAllocations_PaymentId",
                table: "IouAllocations",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_IouEntries_PersonId",
                table: "IouEntries",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_IouEntries_TransactionId",
                table: "IouEntries",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_IouEntries_UserId_PersonId",
                table: "IouEntries",
                columns: new[] { "UserId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_IouPayments_AppliesToEntryId",
                table: "IouPayments",
                column: "AppliesToEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_IouPayments_PersonId",
                table: "IouPayments",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_IouPayments_TransactionId",
                table: "IouPayments",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_IouPayments_UserId_PersonId",
                table: "IouPayments",
                columns: new[] { "UserId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_IouPeople_UserId",
                table: "IouPeople",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IouAllocations");

            migrationBuilder.DropTable(
                name: "IouPayments");

            migrationBuilder.DropTable(
                name: "IouEntries");

            migrationBuilder.DropTable(
                name: "IouPeople");
        }
    }
}
