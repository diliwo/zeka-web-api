using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationTenantConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MonitoringReports_Clients_ClientId",
                table: "MonitoringReports");

            migrationBuilder.DropForeignKey(
                name: "FK_MonitoringReports_SocialWorkers_SocialWorkerId",
                table: "MonitoringReports");

            migrationBuilder.DropForeignKey(
                name: "FK_ProfessionalAssessments_Assessments_AssessmentId",
                table: "ProfessionalAssessments");

            migrationBuilder.DropForeignKey(
                name: "FK_ProfessionnalExperience_Clients_ClientId",
                table: "ProfessionnalExperience");

            migrationBuilder.DropForeignKey(
                name: "FK_SchoolRegistrations_Clients_ClientId",
                table: "SchoolRegistrations");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialCases_Clients_ClientId",
                table: "SocialCases");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialCases_SocialWorkers_SocialWorkerId",
                table: "SocialCases");

            migrationBuilder.DropIndex(
                name: "IX_SocialCases_ClientId",
                table: "SocialCases");

            migrationBuilder.DropIndex(
                name: "IX_SocialCases_SocialWorkerId",
                table: "SocialCases");

            migrationBuilder.DropIndex(
                name: "IX_SchoolRegistrations_ClientId",
                table: "SchoolRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_ProfessionnalExperience_ClientId",
                table: "ProfessionnalExperience");

            migrationBuilder.DropIndex(
                name: "IX_ProfessionalAssessments_AssessmentId",
                table: "ProfessionalAssessments");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringReports_ClientId",
                table: "MonitoringReports");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringReports_SocialWorkerId",
                table: "MonitoringReports");

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF to_regclass('public."__OrganisationTenantMap"') IS NULL THEN
                        RAISE EXCEPTION 'Create and validate __OrganisationTenantMap(TenantName text, OrganisationId uuid) before applying this migration.';
                    END IF;
                END $$;

                ALTER TABLE "SocialWorkers" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "SocialCases" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "SchoolRegistrations" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "ProfessionnalExperience" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "ProfessionalAssessments" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "MonitoringReports" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "Clients" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "Assessments" ADD COLUMN "OrganisationId" uuid;

                UPDATE "SocialWorkers" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "SocialCases" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "SchoolRegistrations" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "ProfessionnalExperience" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "ProfessionalAssessments" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "MonitoringReports" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "Clients" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "Assessments" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";

                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM (
                            SELECT "OrganisationId" FROM "SocialWorkers" UNION ALL SELECT "OrganisationId" FROM "SocialCases"
                            UNION ALL SELECT "OrganisationId" FROM "SchoolRegistrations" UNION ALL SELECT "OrganisationId" FROM "ProfessionnalExperience"
                            UNION ALL SELECT "OrganisationId" FROM "ProfessionalAssessments" UNION ALL SELECT "OrganisationId" FROM "MonitoringReports"
                            UNION ALL SELECT "OrganisationId" FROM "Clients" UNION ALL SELECT "OrganisationId" FROM "Assessments"
                        ) rows WHERE "OrganisationId" IS NULL OR "OrganisationId" = '00000000-0000-0000-0000-000000000000'
                    ) THEN RAISE EXCEPTION 'Tenant backfill is incomplete or contains an empty OrganisationId.'; END IF;
                END $$;

                ALTER TABLE "SocialWorkers" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "SocialCases" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "SchoolRegistrations" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "ProfessionnalExperience" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "ProfessionalAssessments" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "MonitoringReports" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "Clients" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "Assessments" ALTER COLUMN "OrganisationId" SET NOT NULL;

                ALTER TABLE "TrainingType" DROP COLUMN "TenantName";
                ALTER TABLE "TrainingField" DROP COLUMN "TenantName";
                ALTER TABLE "Training" DROP COLUMN "TenantName";
                ALTER TABLE "SocialWorkers" DROP COLUMN "TenantName";
                ALTER TABLE "SocialCases" DROP COLUMN "TenantName";
                ALTER TABLE "SchoolRegistrations" DROP COLUMN "TenantName";
                ALTER TABLE "School" DROP COLUMN "TenantName";
                ALTER TABLE "ProfessionnalExperience" DROP COLUMN "TenantName";
                ALTER TABLE "ProfessionalAssessments" DROP COLUMN "TenantName";
                ALTER TABLE "Profession" DROP COLUMN "TenantName";
                ALTER TABLE "NatureOfContract" DROP COLUMN "TenantName";
                ALTER TABLE "MonitoringReports" DROP COLUMN "TenantName";
                ALTER TABLE "MonitoringActions" DROP COLUMN "TenantName";
                ALTER TABLE "Languages" DROP COLUMN "TenantName";
                ALTER TABLE "Clients" DROP COLUMN "TenantName";
                ALTER TABLE "Assessments" DROP COLUMN "TenantName";
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SocialWorkers_SocialWorkerId_OrganisationId",
                table: "SocialWorkers",
                columns: new[] { "SocialWorkerId", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SocialCases_SchoolRegistrationId_OrganisationId",
                table: "SocialCases",
                columns: new[] { "SchoolRegistrationId", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SchoolRegistrations_SchoolRegistrationId_OrganisationId",
                table: "SchoolRegistrations",
                columns: new[] { "SchoolRegistrationId", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProfessionnalExperience_ProfessionnalExperienceId_Organisat~",
                table: "ProfessionnalExperience",
                columns: new[] { "ProfessionnalExperienceId", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProfessionalAssessments_Id_OrganisationId",
                table: "ProfessionalAssessments",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MonitoringReports_Id_OrganisationId",
                table: "MonitoringReports",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Clients_Id_OrganisationId",
                table: "Clients",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Assessments_AssessmentId_OrganisationId",
                table: "Assessments",
                columns: new[] { "AssessmentId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialWorkers_OrganisationId_UserName",
                table: "SocialWorkers",
                columns: new[] { "OrganisationId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialCases_ClientId_OrganisationId",
                table: "SocialCases",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialCases_SocialWorkerId_OrganisationId",
                table: "SocialCases",
                columns: new[] { "SocialWorkerId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_ClientId_OrganisationId",
                table: "SchoolRegistrations",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionnalExperience_ClientId_OrganisationId",
                table: "ProfessionnalExperience",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalAssessments_AssessmentId_OrganisationId",
                table: "ProfessionalAssessments",
                columns: new[] { "AssessmentId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringReports_ClientId_OrganisationId",
                table: "MonitoringReports",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringReports_SocialWorkerId_OrganisationId",
                table: "MonitoringReports",
                columns: new[] { "SocialWorkerId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_OrganisationId_ReferenceNumber",
                table: "Clients",
                columns: new[] { "OrganisationId", "ReferenceNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoringReports_Clients_ClientId_OrganisationId",
                table: "MonitoringReports",
                columns: new[] { "ClientId", "OrganisationId" },
                principalTable: "Clients",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoringReports_SocialWorkers_SocialWorkerId_Organisation~",
                table: "MonitoringReports",
                columns: new[] { "SocialWorkerId", "OrganisationId" },
                principalTable: "SocialWorkers",
                principalColumns: new[] { "SocialWorkerId", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProfessionalAssessments_Assessments_AssessmentId_Organisati~",
                table: "ProfessionalAssessments",
                columns: new[] { "AssessmentId", "OrganisationId" },
                principalTable: "Assessments",
                principalColumns: new[] { "AssessmentId", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProfessionnalExperience_Clients_ClientId_OrganisationId",
                table: "ProfessionnalExperience",
                columns: new[] { "ClientId", "OrganisationId" },
                principalTable: "Clients",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SchoolRegistrations_Clients_ClientId_OrganisationId",
                table: "SchoolRegistrations",
                columns: new[] { "ClientId", "OrganisationId" },
                principalTable: "Clients",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialCases_Clients_ClientId_OrganisationId",
                table: "SocialCases",
                columns: new[] { "ClientId", "OrganisationId" },
                principalTable: "Clients",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialCases_SocialWorkers_SocialWorkerId_OrganisationId",
                table: "SocialCases",
                columns: new[] { "SocialWorkerId", "OrganisationId" },
                principalTable: "SocialWorkers",
                principalColumns: new[] { "SocialWorkerId", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    RAISE EXCEPTION 'Automatic rollback is disabled because OrganisationId to TenantName is not losslessly reversible. Restore the verified pre-migration backup.';
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_MonitoringReports_Clients_ClientId_OrganisationId",
                table: "MonitoringReports");

            migrationBuilder.DropForeignKey(
                name: "FK_MonitoringReports_SocialWorkers_SocialWorkerId_Organisation~",
                table: "MonitoringReports");

            migrationBuilder.DropForeignKey(
                name: "FK_ProfessionalAssessments_Assessments_AssessmentId_Organisati~",
                table: "ProfessionalAssessments");

            migrationBuilder.DropForeignKey(
                name: "FK_ProfessionnalExperience_Clients_ClientId_OrganisationId",
                table: "ProfessionnalExperience");

            migrationBuilder.DropForeignKey(
                name: "FK_SchoolRegistrations_Clients_ClientId_OrganisationId",
                table: "SchoolRegistrations");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialCases_Clients_ClientId_OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropForeignKey(
                name: "FK_SocialCases_SocialWorkers_SocialWorkerId_OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SocialWorkers_SocialWorkerId_OrganisationId",
                table: "SocialWorkers");

            migrationBuilder.DropIndex(
                name: "IX_SocialWorkers_OrganisationId_UserName",
                table: "SocialWorkers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SocialCases_SchoolRegistrationId_OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropIndex(
                name: "IX_SocialCases_ClientId_OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropIndex(
                name: "IX_SocialCases_SocialWorkerId_OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SchoolRegistrations_SchoolRegistrationId_OrganisationId",
                table: "SchoolRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_SchoolRegistrations_ClientId_OrganisationId",
                table: "SchoolRegistrations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProfessionnalExperience_ProfessionnalExperienceId_Organisat~",
                table: "ProfessionnalExperience");

            migrationBuilder.DropIndex(
                name: "IX_ProfessionnalExperience_ClientId_OrganisationId",
                table: "ProfessionnalExperience");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProfessionalAssessments_Id_OrganisationId",
                table: "ProfessionalAssessments");

            migrationBuilder.DropIndex(
                name: "IX_ProfessionalAssessments_AssessmentId_OrganisationId",
                table: "ProfessionalAssessments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MonitoringReports_Id_OrganisationId",
                table: "MonitoringReports");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringReports_ClientId_OrganisationId",
                table: "MonitoringReports");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringReports_SocialWorkerId_OrganisationId",
                table: "MonitoringReports");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Clients_Id_OrganisationId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Clients_OrganisationId_ReferenceNumber",
                table: "Clients");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Assessments_AssessmentId_OrganisationId",
                table: "Assessments");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "SocialWorkers");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "SocialCases");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "SchoolRegistrations");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "ProfessionnalExperience");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "ProfessionalAssessments");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "MonitoringReports");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Assessments");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "TrainingType",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "TrainingField",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Training",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "SocialWorkers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "SocialCases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "SchoolRegistrations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "School",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "ProfessionnalExperience",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "ProfessionalAssessments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Profession",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "NatureOfContract",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "MonitoringReports",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "MonitoringActions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Languages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Clients",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Assessments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SocialCases_ClientId",
                table: "SocialCases",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_SocialCases_SocialWorkerId",
                table: "SocialCases",
                column: "SocialWorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_ClientId",
                table: "SchoolRegistrations",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionnalExperience_ClientId",
                table: "ProfessionnalExperience",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionalAssessments_AssessmentId",
                table: "ProfessionalAssessments",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringReports_ClientId",
                table: "MonitoringReports",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringReports_SocialWorkerId",
                table: "MonitoringReports",
                column: "SocialWorkerId");

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoringReports_Clients_ClientId",
                table: "MonitoringReports",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MonitoringReports_SocialWorkers_SocialWorkerId",
                table: "MonitoringReports",
                column: "SocialWorkerId",
                principalTable: "SocialWorkers",
                principalColumn: "SocialWorkerId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProfessionalAssessments_Assessments_AssessmentId",
                table: "ProfessionalAssessments",
                column: "AssessmentId",
                principalTable: "Assessments",
                principalColumn: "AssessmentId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ProfessionnalExperience_Clients_ClientId",
                table: "ProfessionnalExperience",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SchoolRegistrations_Clients_ClientId",
                table: "SchoolRegistrations",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialCases_Clients_ClientId",
                table: "SocialCases",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SocialCases_SocialWorkers_SocialWorkerId",
                table: "SocialCases",
                column: "SocialWorkerId",
                principalTable: "SocialWorkers",
                principalColumn: "SocialWorkerId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
