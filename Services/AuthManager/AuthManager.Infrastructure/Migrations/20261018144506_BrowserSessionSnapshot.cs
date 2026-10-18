using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BrowserSessionSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.CreateTable(
                name: "BrowserSessionFamilies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowserSessionFamilies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrowserSessionFamilies_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BrowserRefreshCredentials",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowserRefreshCredentials", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_BrowserRefreshCredentials_BrowserSessionFamilies_FamilyId",
                        column: x => x.FamilyId,
                        principalTable: "BrowserSessionFamilies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrowserRefreshCredentials_FamilyId",
                table: "BrowserRefreshCredentials",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_BrowserSessionFamilies_UserId",
                table: "BrowserSessionFamilies",
                column: "UserId");
            migrationBuilder.Sql("""
                ALTER TABLE "BrowserSessionFamilies" ADD CONSTRAINT "CK_BrowserSessionFamily_Deadlines"
                  CHECK ("CreatedAtUtc" < "IdleExpiresAtUtc" AND "IdleExpiresAtUtc" <= "AbsoluteExpiresAtUtc");
                ALTER TABLE "BrowserRefreshCredentials" ADD CONSTRAINT "CK_BrowserRefreshCredential_Hash"
                  CHECK ("Hash" ~ '^[0-9A-F]{64}$');
                -- Tenant-neutral Identity state. Never grants organisation or permission authority.
                GRANT SELECT, INSERT ON "BrowserRefreshCredentials", "BrowserSessionFamilies" TO zeka_auth_runtime;
                GRANT UPDATE ("ConsumedAtUtc") ON "BrowserRefreshCredentials" TO zeka_auth_runtime;
                GRANT UPDATE ("IdleExpiresAtUtc", "RevokedAtUtc") ON "BrowserSessionFamilies" TO zeka_auth_runtime;
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Session security evidence rollback requires explicit approval.");
        }
    }
}
