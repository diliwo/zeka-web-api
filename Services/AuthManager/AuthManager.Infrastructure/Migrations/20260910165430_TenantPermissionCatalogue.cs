using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantPermissionCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF (SELECT count(*) FROM "PermissionSets" WHERE
                        ("Id" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a01' AND "Code" = 'OrganisationOwner') OR
                        ("Id" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a02' AND "Code" = 'OrganisationAdministrator')) <> 2 THEN
                        RAISE EXCEPTION 'Owner/Admin permission identities do not match the reviewed baseline';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "OrganisationMemberships" WHERE
                        "PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a03') THEN
                        IF to_regclass('"__MembershipRoleMap"') IS NULL THEN
                            RAISE EXCEPTION 'Legacy Member assignments require an approved __MembershipRoleMap';
                        END IF;
                        IF EXISTS (
                            SELECT m."Id" FROM "OrganisationMemberships" m
                            LEFT JOIN "__MembershipRoleMap" r ON r."MembershipId" = m."Id" AND r."OrganisationId" = m."OrganisationId"
                            WHERE m."PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a03'
                            GROUP BY m."Id" HAVING count(r."RoleCode") <> 1 OR bool_or(r."RoleCode" NOT IN
                                ('LimitedViewer', 'LimitedEditor', 'Viewer', 'Contributor', 'Editor'))) THEN
                            RAISE EXCEPTION 'Missing, ambiguous, or unknown legacy Member role mapping';
                        END IF;
                    END IF;
                END $$;
                """);
            migrationBuilder.UpdateData(
                table: "PermissionSets",
                keyColumn: "Id",
                keyValue: new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a03"),
                column: "IsSystem", value: false);

            migrationBuilder.UpdateData(
                table: "PermissionSets",
                keyColumn: "Id",
                keyValue: new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a01"),
                columns: new[] { "Code", "DisplayName" },
                values: new object[] { "Owner", "Owner" });

            migrationBuilder.UpdateData(
                table: "PermissionSets",
                keyColumn: "Id",
                keyValue: new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a02"),
                columns: new[] { "Code", "DisplayName" },
                values: new object[] { "Admin", "Admin" });

            migrationBuilder.InsertData(
                table: "PermissionSets",
                columns: new[] { "Id", "Code", "DisplayName", "IsSystem" },
                values: new object[,]
                {
                    { new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a04"), "LimitedViewer", "Limited Viewer", true },
                    { new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a05"), "LimitedEditor", "Limited Editor", true },
                    { new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a06"), "Viewer", "Viewer", true },
                    { new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a07"), "Contributor", "Contributor", true },
                    { new Guid("d8eceeeb-b796-4d77-92a5-6a11fab10a08"), "Editor", "Editor", true }
                });
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF to_regclass('"__MembershipRoleMap"') IS NOT NULL THEN
                        UPDATE "OrganisationMemberships" m SET "PermissionSetId" = p."Id", "ConcurrencyVersion" = m."ConcurrencyVersion" + 1
                        FROM "__MembershipRoleMap" r JOIN "PermissionSets" p ON p."Code" = r."RoleCode" AND p."IsSystem"
                        WHERE r."MembershipId" = m."Id" AND r."OrganisationId" = m."OrganisationId"
                            AND m."PermissionSetId" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a03';
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Automatic rollback is disabled; permission assignments require reviewed recovery'; END $$;");
    }
}
