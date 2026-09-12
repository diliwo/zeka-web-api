using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ClientManagement.Infrastructure.Persistence;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations;

[DbContext(typeof(DeploymentDbContext))]
[Migration("20260912180001_PostgreSqlRlsAndRuntimeRoles")]
public sealed class PostgreSqlRlsAndRuntimeRoles : Migration
{
    private static readonly string[] Tables =
    [
        "Clients",
        "SocialWorkers",
        "SocialCases",
        "Assessments",
        "ProfessionalAssessments",
        "ProfessionnalExperience",
        "SchoolRegistrations",
        "MonitoringReports"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $roles$
            BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_client_owner')
                 OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_client_migrator')
                 OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_client_runtime') THEN
                RAISE EXCEPTION 'Required ClientManagement database roles are not provisioned.';
              END IF;
            END $roles$;
            SET ROLE zeka_client_owner;
            DO $schema$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_namespace WHERE nspname='zeka') THEN
                RAISE EXCEPTION 'Required zeka schema is not provisioned.';
              END IF;
            END $schema$;
            REVOKE ALL ON SCHEMA zeka FROM PUBLIC;
            GRANT USAGE ON SCHEMA zeka TO zeka_client_runtime;
            CREATE OR REPLACE FUNCTION zeka.current_organisation_id() RETURNS uuid
            LANGUAGE plpgsql SECURITY INVOKER
            SET search_path = pg_catalog
            AS $function$
            DECLARE raw text; parsed uuid;
            BEGIN
              raw := current_setting('zeka.organisation_id', true);
              IF raw IS NULL OR raw = '' THEN RAISE EXCEPTION 'Tenant database context is invalid.'; END IF;
              BEGIN parsed := raw::uuid;
              EXCEPTION WHEN invalid_text_representation THEN RAISE EXCEPTION 'Tenant database context is invalid.';
              END;
              IF raw <> lower(parsed::text) OR parsed = '00000000-0000-0000-0000-000000000000'::uuid THEN
                RAISE EXCEPTION 'Tenant database context is invalid.';
              END IF;
              RETURN parsed;
            END $function$;
            REVOKE ALL ON FUNCTION zeka.current_organisation_id() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_client_runtime;
            DROP TABLE IF EXISTS "__StaffMembershipMap";
            DROP TABLE IF EXISTS "__OrganisationTenantMap";
            """);
        foreach (var table in Tables)
        {
            var policy = $"rls_{table.ToLowerInvariant()}_organisation";
            migrationBuilder.Sql($$"""
                ALTER TABLE "{{table}}" OWNER TO zeka_client_owner;
                ALTER TABLE "{{table}}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{{table}}" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS {{policy}} ON "{{table}}";
                CREATE POLICY {{policy}} ON "{{table}}" FOR ALL TO zeka_client_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE "{{table}}" FROM PUBLIC;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "{{table}}" TO zeka_client_runtime;
                """);
        }
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM zeka_client_runtime;
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "__EFMigrationsHistory" TO zeka_client_migrator;
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
              "Clients", "SocialWorkers", "SocialCases", "Assessments", "ProfessionalAssessments",
              "ProfessionnalExperience", "SchoolRegistrations", "MonitoringReports", "Languages",
              "MonitoringActions", "NatureOfContract", "Profession", "School", "Training", "TrainingField",
              "TrainingType" TO zeka_client_runtime;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO zeka_client_runtime;
            GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_client_runtime;
            RESET ROLE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("RLS rollback requires an explicitly approved incident procedure.");
}
