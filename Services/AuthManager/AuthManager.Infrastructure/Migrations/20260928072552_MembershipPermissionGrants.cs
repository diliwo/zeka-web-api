using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MembershipPermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_OrganisationMemberships_Id_OrganisationId",
                table: "OrganisationMemberships",
                columns: new[] { "Id", "OrganisationId" });

            migrationBuilder.CreateTable(
                name: "MembershipPermissionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    GrantedByMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedBySubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedByMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedBySubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MembershipPermissionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MembershipPermissionGrants_OrganisationMemberships_GrantedB~",
                        columns: x => new { x.GrantedByMembershipId, x.OrganisationId },
                        principalTable: "OrganisationMemberships",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipPermissionGrants_OrganisationMemberships_Organisa~",
                        columns: x => new { x.OrganisationMembershipId, x.OrganisationId },
                        principalTable: "OrganisationMemberships",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MembershipPermissionGrants_OrganisationMemberships_RevokedB~",
                        columns: x => new { x.RevokedByMembershipId, x.OrganisationId },
                        principalTable: "OrganisationMemberships",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPermissionGrants_GrantedByMembershipId_Organisati~",
                table: "MembershipPermissionGrants",
                columns: new[] { "GrantedByMembershipId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPermissionGrants_OrganisationId_GrantedAtUtc",
                table: "MembershipPermissionGrants",
                columns: new[] { "OrganisationId", "GrantedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPermissionGrants_OrganisationMembershipId_Organis~",
                table: "MembershipPermissionGrants",
                columns: new[] { "OrganisationMembershipId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPermissionGrants_OrganisationMembershipId_Permiss~",
                table: "MembershipPermissionGrants",
                columns: new[] { "OrganisationMembershipId", "PermissionKey" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MembershipPermissionGrants_RevokedByMembershipId_Organisati~",
                table: "MembershipPermissionGrants",
                columns: new[] { "RevokedByMembershipId", "OrganisationId" });

            migrationBuilder.Sql("""
                ALTER TABLE public."MembershipPermissionGrants" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."MembershipPermissionGrants" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_membershippermissiongrants_organisation
                  ON public."MembershipPermissionGrants" TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());

                REVOKE ALL ON TABLE public."MembershipPermissionGrants" FROM PUBLIC, zeka_auth_runtime;
                GRANT SELECT, INSERT ON TABLE public."MembershipPermissionGrants" TO zeka_auth_runtime;
                GRANT UPDATE ("RevokedByMembershipId", "RevokedBySubjectId", "RevokedAtUtc", "ConcurrencyVersion")
                  ON TABLE public."MembershipPermissionGrants" TO zeka_auth_runtime;
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException(
                "Permission-grant audit history removal requires a separately reviewed migration.");
    }
}
