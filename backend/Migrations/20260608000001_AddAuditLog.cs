using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneyTracker.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id         = table.Column<long>(nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId     = table.Column<string>(nullable: true),
                    UserEmail  = table.Column<string>(nullable: true),
                    Action     = table.Column<string>(nullable: false),
                    EntityType = table.Column<string>(nullable: true),
                    EntityId   = table.Column<int>(nullable: true),
                    Details    = table.Column<string>(nullable: true),
                    IpAddress  = table.Column<string>(nullable: true),
                    Timestamp  = table.Column<DateTime>(nullable: false),
                    IsSystem   = table.Column<bool>(nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_AuditLogs", x => x.Id));

            migrationBuilder.CreateIndex("IX_AuditLogs_Timestamp", "AuditLogs", "Timestamp");
            migrationBuilder.CreateIndex("IX_AuditLogs_UserId_Timestamp", "AuditLogs", new[] { "UserId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AuditLogs");
        }
    }
}
