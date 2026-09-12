using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitTenantEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "OrganisationMembershipId", table: "StaffMembers", type: "uuid", nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProjectionVersion",
                table: "StaffMembers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "StaffProjectionOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffProjectionOutbox", x => x.Id);
                    table.UniqueConstraint("AK_StaffProjectionOutbox_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                });

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "StaffMembers") THEN
                        IF to_regclass('"__StaffMembershipMap"') IS NULL THEN
                            RAISE EXCEPTION 'Reviewed __StaffMembershipMap is required; never infer membership from staff names';
                        END IF;
                        IF EXISTS (
                            SELECT s."Id" FROM "StaffMembers" s
                            LEFT JOIN "__StaffMembershipMap" m ON m."LocalId" = s."Id" AND m."OrganisationId" = s."OrganisationId"
                            GROUP BY s."Id"
                            HAVING count(m."OrganisationMembershipId") <> 1
                                OR bool_or(m."OrganisationMembershipId" = '00000000-0000-0000-0000-000000000000'))
                            OR EXISTS (SELECT "OrganisationId", "OrganisationMembershipId" FROM "__StaffMembershipMap"
                                GROUP BY "OrganisationId", "OrganisationMembershipId" HAVING count(*) > 1) THEN
                            RAISE EXCEPTION 'Missing, duplicate, empty, or cross-organisation membership mapping';
                        END IF;
                        UPDATE "StaffMembers" s SET "OrganisationMembershipId" = m."OrganisationMembershipId"
                            FROM "__StaffMembershipMap" m
                            WHERE m."LocalId" = s."Id" AND m."OrganisationId" = s."OrganisationId";
                    END IF;
                END $$;
                ALTER TABLE "StaffMembers" ALTER COLUMN "OrganisationMembershipId" SET NOT NULL;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_OrganisationId_OrganisationMembershipId",
                table: "StaffMembers",
                columns: new[] { "OrganisationId", "OrganisationMembershipId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StaffMembers_Membership",
                table: "StaffMembers",
                sql: "\"OrganisationMembershipId\" <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "IX_StaffProjectionOutbox_OrganisationId_EventId",
                table: "StaffProjectionOutbox",
                columns: new[] { "OrganisationId", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Automatic rollback is disabled; restore a reviewed backup and reconcile membership and projection data'; END $$;");
    }
}
