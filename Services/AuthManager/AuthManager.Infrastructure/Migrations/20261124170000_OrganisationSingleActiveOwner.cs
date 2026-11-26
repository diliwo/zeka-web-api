using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AuthManager.Infrastructure.Migrations;

public partial class OrganisationSingleActiveOwner : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This is the existing admitted owner role; no role or privilege is created.
        migrationBuilder.Sql("SET LOCAL ROLE zeka_auth_owner;");
        migrationBuilder.Sql("LOCK TABLE public.\"OrganisationMemberships\" IN SHARE MODE;");
        migrationBuilder.Sql("""
            DO $active_owner_precheck$
            BEGIN
              IF EXISTS (
                SELECT 1 FROM public."OrganisationMemberships"
                WHERE "PermissionSetId"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid AND "Status"=1
                GROUP BY "OrganisationId" HAVING count(*)>1)
              THEN RAISE EXCEPTION 'AUTH_ACTIVE_OWNER_CONFLICTS_PRESENT' USING ERRCODE='23505';
              END IF;
            END $active_owner_precheck$;
            """);
        migrationBuilder.CreateIndex(
            name: "UX_OrganisationMemberships_ActiveOwner",
            table: "OrganisationMemberships",
            column: "OrganisationId",
            unique: true,
            filter: "\"PermissionSetId\" = 'D8ECEEEB-B796-4D77-92A5-6A11FAB10A01' AND \"Status\" = 1");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new System.InvalidOperationException("Active-owner protection requires reviewed recovery; automatic rollback is disabled.");
}
