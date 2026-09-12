using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitTenantEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastProjectionEventId",
                table: "SocialWorkers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(name: "OrganisationMembershipId", table: "SocialWorkers", type: "uuid", nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProjectionVersion",
                table: "SocialWorkers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "Assessments",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "SocialWorkers") THEN
                        IF to_regclass('"__StaffMembershipMap"') IS NULL THEN
                            RAISE EXCEPTION 'Reviewed __StaffMembershipMap is required; never infer membership from staff names';
                        END IF;
                        IF EXISTS (
                            SELECT s."SocialWorkerId" FROM "SocialWorkers" s
                            LEFT JOIN "__StaffMembershipMap" m ON m."LocalId" = s."SocialWorkerId" AND m."OrganisationId" = s."OrganisationId"
                            GROUP BY s."SocialWorkerId"
                            HAVING count(m."OrganisationMembershipId") <> 1
                                OR bool_or(m."OrganisationMembershipId" = '00000000-0000-0000-0000-000000000000'))
                            OR EXISTS (SELECT "OrganisationId", "OrganisationMembershipId" FROM "__StaffMembershipMap"
                                GROUP BY "OrganisationId", "OrganisationMembershipId" HAVING count(*) > 1) THEN
                            RAISE EXCEPTION 'Missing, duplicate, empty, or cross-organisation membership mapping';
                        END IF;
                        UPDATE "SocialWorkers" s SET "OrganisationMembershipId" = m."OrganisationMembershipId"
                            FROM "__StaffMembershipMap" m
                            WHERE m."LocalId" = s."SocialWorkerId" AND m."OrganisationId" = s."OrganisationId";
                    END IF;
                END $$;
                ALTER TABLE "SocialWorkers" ALTER COLUMN "OrganisationMembershipId" SET NOT NULL;
                """);
            migrationBuilder.CreateIndex(
                name: "IX_SocialWorkers_OrganisationId_OrganisationMembershipId",
                table: "SocialWorkers",
                columns: new[] { "OrganisationId", "OrganisationMembershipId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SocialWorkers_Membership",
                table: "SocialWorkers",
                sql: "\"OrganisationMembershipId\" <> '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "IX_Assessments_ClientId_OrganisationId",
                table: "Assessments",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Assessments_Clients_ClientId_OrganisationId",
                table: "Assessments",
                columns: new[] { "ClientId", "OrganisationId" },
                principalTable: "Clients",
                principalColumns: new[] { "Id", "OrganisationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Automatic rollback is disabled; restore a reviewed backup and reconcile membership and projection data'; END $$;");
    }
}
