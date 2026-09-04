using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AdminAreaManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationTenantConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContactPersons_Partners_PartnerId",
                table: "ContactPersons");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentPartners_Partners_PartnerId",
                table: "DocumentPartners");

            migrationBuilder.DropForeignKey(
                name: "FK_Emails_Partners_PartnerId",
                table: "Emails");

            migrationBuilder.DropForeignKey(
                name: "FK_Partners_StaffMembers_StaffMemberId",
                table: "Partners");

            migrationBuilder.DropForeignKey(
                name: "FK_StaffMembers_Teams_TeamId",
                table: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_StaffMembers_TeamId",
                table: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_Partners_StaffMemberId",
                table: "Partners");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Emails",
                table: "Emails");

            migrationBuilder.DropIndex(
                name: "IX_DocumentPartners_PartnerId",
                table: "DocumentPartners");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ContactPersons",
                table: "ContactPersons");

            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF to_regclass('public."__OrganisationTenantMap"') IS NULL THEN
                        RAISE EXCEPTION 'Create and validate __OrganisationTenantMap(TenantName text, OrganisationId uuid) before applying this migration.';
                    END IF;
                END $$;

                ALTER TABLE "Teams" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "StaffMembers" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "Partners" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "DocumentPartners" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "Emails" ADD COLUMN "OrganisationId" uuid;
                ALTER TABLE "ContactPersons" ADD COLUMN "OrganisationId" uuid;

                UPDATE "Teams" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "StaffMembers" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "Partners" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "DocumentPartners" t SET "OrganisationId" = m."OrganisationId" FROM "__OrganisationTenantMap" m WHERE t."TenantName" = m."TenantName";
                UPDATE "Emails" child SET "OrganisationId" = parent."OrganisationId" FROM "Partners" parent WHERE child."PartnerId" = parent."Id";
                UPDATE "ContactPersons" child SET "OrganisationId" = parent."OrganisationId" FROM "Partners" parent WHERE child."PartnerId" = parent."Id";

                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM (
                            SELECT "OrganisationId" FROM "Teams" UNION ALL SELECT "OrganisationId" FROM "StaffMembers"
                            UNION ALL SELECT "OrganisationId" FROM "Partners" UNION ALL SELECT "OrganisationId" FROM "DocumentPartners"
                            UNION ALL SELECT "OrganisationId" FROM "Emails" UNION ALL SELECT "OrganisationId" FROM "ContactPersons"
                        ) rows WHERE "OrganisationId" IS NULL OR "OrganisationId" = '00000000-0000-0000-0000-000000000000'
                    ) THEN RAISE EXCEPTION 'Tenant backfill is incomplete or contains an empty OrganisationId.'; END IF;
                END $$;

                ALTER TABLE "Teams" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "StaffMembers" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "Partners" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "DocumentPartners" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "Emails" ALTER COLUMN "OrganisationId" SET NOT NULL;
                ALTER TABLE "ContactPersons" ALTER COLUMN "OrganisationId" SET NOT NULL;

                ALTER TABLE "Trainings" DROP COLUMN "TenantName";
                ALTER TABLE "TrainingTypes" DROP COLUMN "TenantName";
                ALTER TABLE "TrainingFields" DROP COLUMN "TenantName";
                ALTER TABLE "Teams" DROP COLUMN "TenantName";
                ALTER TABLE "StaffMembers" DROP COLUMN "TenantName";
                ALTER TABLE "Schools" DROP COLUMN "TenantName";
                ALTER TABLE "Professions" DROP COLUMN "TenantName";
                ALTER TABLE "Partners" DROP COLUMN "TenantName";
                ALTER TABLE "Nationalities" DROP COLUMN "TenantName";
                ALTER TABLE "DocumentPartners" DROP COLUMN "TenantName";
                ALTER TABLE "Cities" DROP COLUMN "TenantName";
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Teams_Id_OrganisationId",
                table: "Teams",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_StaffMembers_Id_OrganisationId",
                table: "StaffMembers",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Partners_Id_OrganisationId",
                table: "Partners",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Emails",
                table: "Emails",
                columns: new[] { "PartnerId", "OrganisationId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DocumentPartners_Id_OrganisationId",
                table: "DocumentPartners",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ContactPersons",
                table: "ContactPersons",
                columns: new[] { "PartnerId", "OrganisationId", "ContactDetails", "Gender", "ToDelete" });

            migrationBuilder.CreateIndex(
                name: "IX_Teams_OrganisationId_Acronym",
                table: "Teams",
                columns: new[] { "OrganisationId", "Acronym" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_OrganisationId_UserName",
                table: "StaffMembers",
                columns: new[] { "OrganisationId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_TeamId_OrganisationId",
                table: "StaffMembers",
                columns: new[] { "TeamId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Partners_OrganisationId_PartnerNumber",
                table: "Partners",
                columns: new[] { "OrganisationId", "PartnerNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Partners_StaffMemberId_OrganisationId",
                table: "Partners",
                columns: new[] { "StaffMemberId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPartners_PartnerId_OrganisationId",
                table: "DocumentPartners",
                columns: new[] { "PartnerId", "OrganisationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ContactPersons_Partners_PartnerId_OrganisationId",
                table: "ContactPersons",
                columns: new[] { "PartnerId", "OrganisationId" },
                principalTable: "Partners",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentPartners_Partners_PartnerId_OrganisationId",
                table: "DocumentPartners",
                columns: new[] { "PartnerId", "OrganisationId" },
                principalTable: "Partners",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Emails_Partners_PartnerId_OrganisationId",
                table: "Emails",
                columns: new[] { "PartnerId", "OrganisationId" },
                principalTable: "Partners",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_StaffMembers_StaffMemberId_OrganisationId",
                table: "Partners",
                columns: new[] { "StaffMemberId", "OrganisationId" },
                principalTable: "StaffMembers",
                principalColumns: new[] { "Id", "OrganisationId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffMembers_Teams_TeamId_OrganisationId",
                table: "StaffMembers",
                columns: new[] { "TeamId", "OrganisationId" },
                principalTable: "Teams",
                principalColumns: new[] { "Id", "OrganisationId" },
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
                name: "FK_ContactPersons_Partners_PartnerId_OrganisationId",
                table: "ContactPersons");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentPartners_Partners_PartnerId_OrganisationId",
                table: "DocumentPartners");

            migrationBuilder.DropForeignKey(
                name: "FK_Emails_Partners_PartnerId_OrganisationId",
                table: "Emails");

            migrationBuilder.DropForeignKey(
                name: "FK_Partners_StaffMembers_StaffMemberId_OrganisationId",
                table: "Partners");

            migrationBuilder.DropForeignKey(
                name: "FK_StaffMembers_Teams_TeamId_OrganisationId",
                table: "StaffMembers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Teams_Id_OrganisationId",
                table: "Teams");

            migrationBuilder.DropIndex(
                name: "IX_Teams_OrganisationId_Acronym",
                table: "Teams");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_StaffMembers_Id_OrganisationId",
                table: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_StaffMembers_OrganisationId_UserName",
                table: "StaffMembers");

            migrationBuilder.DropIndex(
                name: "IX_StaffMembers_TeamId_OrganisationId",
                table: "StaffMembers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Partners_Id_OrganisationId",
                table: "Partners");

            migrationBuilder.DropIndex(
                name: "IX_Partners_OrganisationId_PartnerNumber",
                table: "Partners");

            migrationBuilder.DropIndex(
                name: "IX_Partners_StaffMemberId_OrganisationId",
                table: "Partners");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Emails",
                table: "Emails");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DocumentPartners_Id_OrganisationId",
                table: "DocumentPartners");

            migrationBuilder.DropIndex(
                name: "IX_DocumentPartners_PartnerId_OrganisationId",
                table: "DocumentPartners");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ContactPersons",
                table: "ContactPersons");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "StaffMembers");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Emails");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "DocumentPartners");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "ContactPersons");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Trainings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "TrainingTypes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "TrainingFields",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Teams",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "StaffMembers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Schools",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Professions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Partners",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Nationalities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "DocumentPartners",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TenantName",
                table: "Cities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Emails",
                table: "Emails",
                columns: new[] { "PartnerId", "Id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_ContactPersons",
                table: "ContactPersons",
                columns: new[] { "PartnerId", "ContactDetails", "Gender", "ToDelete" });

            migrationBuilder.InsertData(
                table: "Cities",
                columns: new[] { "CityId", "Country", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, "Belgium", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Brussels", false, "Zeka" },
                    { 2, "Norway", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Oslo", false, "Zeka" },
                    { 3, "South Africa", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Cape Town", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "Nationalities",
                columns: new[] { "NationalityId", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Belgian", false, "Zeka" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Norway", false, "Zeka" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "South African", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "Professions",
                columns: new[] { "Id", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Kitchen, assistant", false, "Zeka" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Baker", false, "Zeka" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Computer repair technicien", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "Schools",
                columns: new[] { "Id", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Locality", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Cambridge", "Massachusetts Institute Of Technology", false, "Zeka" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Stanford", "Stanford University", false, "Zeka" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Berlin", "International University of Applied Science", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "Teams",
                columns: new[] { "Id", "Acronym", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, "SES", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "School & Education Service", false, "Zeka" },
                    { 2, "IS", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Imigration Service", false, "Zeka" },
                    { 3, "SPI", new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Socio-Professional Integration Service", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "TrainingFields",
                columns: new[] { "TrainingFieldId", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Political Sociology", false, "Zeka" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Information Technology", false, "Zeka" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Languages", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "TrainingTypes",
                columns: new[] { "TrainingTypeId", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Bachelor", false, "Zeka" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Master", false, "Zeka" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Master of Business Administration", false, "Zeka" }
                });

            migrationBuilder.InsertData(
                table: "StaffMembers",
                columns: new[] { "Id", "Created", "CreatedBy", "FirstName", "LastModified", "LastModifiedBy", "LastName", "Softdelete", "TeamId", "TenantName", "UserName" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", "John", null, "", "Doe", false, 1, "Zeka", "Cambridge" },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", "Helen", null, "", "Ripley", false, 3, "Zeka", "hripley" },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", "Adama", null, "", "Rezegova", false, 2, "Zeka", "Cambridge" }
                });

            migrationBuilder.InsertData(
                table: "Trainings",
                columns: new[] { "Id", "Created", "CreatedBy", "LastModified", "LastModifiedBy", "Name", "Softdelete", "TenantName", "TrainingFieldId" },
                values: new object[,]
                {
                    { 1, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Software Developement", false, "Zeka", 2 },
                    { 2, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "English", false, "Zeka", 3 },
                    { 3, new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), "", null, "", "Deutch", false, "Zeka", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMembers_TeamId",
                table: "StaffMembers",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Partners_StaffMemberId",
                table: "Partners",
                column: "StaffMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPartners_PartnerId",
                table: "DocumentPartners",
                column: "PartnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_ContactPersons_Partners_PartnerId",
                table: "ContactPersons",
                column: "PartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentPartners_Partners_PartnerId",
                table: "DocumentPartners",
                column: "PartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Emails_Partners_PartnerId",
                table: "Emails",
                column: "PartnerId",
                principalTable: "Partners",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_StaffMembers_StaffMemberId",
                table: "Partners",
                column: "StaffMemberId",
                principalTable: "StaffMembers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_StaffMembers_Teams_TeamId",
                table: "StaffMembers",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
