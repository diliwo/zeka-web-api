using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Issue78OrganisationTimeZoneCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.AddColumn<long>(
                name: "TimeZoneConfigVersion",
                table: "Organisations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Organisations",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TimeZoneConfigurationAudits",
                columns: table => new
                {
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerKeyId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ExecutorSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewerSubject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReleaseEvidenceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangeReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PreviousTimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PreviousVersion = table.Column<long>(type: "bigint", nullable: false),
                    NewVersion = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeZoneConfigurationAudits", x => x.ApprovalId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeZoneConfigurationAudits_OrganisationId_OccurredAtUtc",
                table: "TimeZoneConfigurationAudits",
                columns: new[] { "OrganisationId", "OccurredAtUtc" });
            migrationBuilder.Sql("""
                ALTER TABLE public."Organisations"
                  ADD CONSTRAINT ck_organisations_timezone_version
                  CHECK (("TimeZoneId" IS NULL AND "TimeZoneConfigVersion" = 0)
                    OR ("TimeZoneId" IS NOT NULL AND "TimeZoneConfigVersion" > 0));
                ALTER TABLE public."TimeZoneConfigurationAudits" OWNER TO zeka_auth_owner;
                ALTER TABLE public."TimeZoneConfigurationAudits" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."TimeZoneConfigurationAudits" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_timezoneconfigurationaudits_organisation
                  ON public."TimeZoneConfigurationAudits" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE public."TimeZoneConfigurationAudits"
                  FROM PUBLIC, zeka_auth_runtime;
                -- The earlier table-level grants would automatically cover new columns.
                -- Preserve the old column set, but make timezone writes unavailable to runtime.
                REVOKE INSERT, UPDATE ON TABLE public."Organisations" FROM zeka_auth_runtime;
                GRANT INSERT ("Id", "DisplayName", "OwnerUserId", "Status", "Slug",
                  "CreatedAtUtc", "UpdatedAtUtc", "ConcurrencyVersion")
                  ON TABLE public."Organisations" TO zeka_auth_runtime;
                GRANT UPDATE ("Id", "DisplayName", "OwnerUserId", "Status", "Slug",
                  "CreatedAtUtc", "UpdatedAtUtc", "ConcurrencyVersion")
                  ON TABLE public."Organisations" TO zeka_auth_runtime;
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Time zone audit/approval evidence and its privilege boundary cannot be dropped automatically.");
        }
    }
}
