using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using AdminAreaManagement.Infrastructure.Persistence;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations;

[DbContext(typeof(DeploymentDbContext))]
[Migration("20260912180000_PostgreSqlRlsAndRuntimeRoles")]
public sealed class PostgreSqlRlsAndRuntimeRoles : Migration
{
    private static readonly string[] Tables =
    [
        "Teams",
        "StaffMembers",
        "Partners",
        "DocumentPartners",
        "ContactPersons",
        "Emails",
        "StaffProjectionOutbox"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $roles$
            BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_adminarea_owner')
                 OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_adminarea_migrator')
                 OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_adminarea_runtime') THEN
                RAISE EXCEPTION 'Required AdminArea database roles are not provisioned.';
              END IF;
            END $roles$;
            SET ROLE zeka_adminarea_owner;
            DO $schema$ BEGIN
              IF NOT EXISTS (SELECT FROM pg_namespace WHERE nspname='zeka') THEN
                RAISE EXCEPTION 'Required zeka schema is not provisioned.';
              END IF;
            END $schema$;
            REVOKE ALL ON SCHEMA zeka FROM PUBLIC;
            GRANT USAGE ON SCHEMA zeka TO zeka_adminarea_runtime;
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
            GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_adminarea_runtime;
            DROP TABLE IF EXISTS "__StaffMembershipMap";
            DROP TABLE IF EXISTS "__OrganisationTenantMap";
            """);
        foreach (var table in Tables)
        {
            var policy = $"rls_{table.ToLowerInvariant()}_organisation";
            migrationBuilder.Sql($$"""
                ALTER TABLE "{{table}}" OWNER TO zeka_adminarea_owner;
                ALTER TABLE "{{table}}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "{{table}}" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS {{policy}} ON "{{table}}";
                CREATE POLICY {{policy}} ON "{{table}}" FOR ALL TO zeka_adminarea_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE "{{table}}" FROM PUBLIC;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "{{table}}" TO zeka_adminarea_runtime;
                """);
        }
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM zeka_adminarea_runtime;
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "__EFMigrationsHistory" TO zeka_adminarea_migrator;
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
              "Teams", "StaffMembers", "Partners", "DocumentPartners", "ContactPersons", "Emails",
              "StaffProjectionOutbox", "Cities", "Nationalities", "Professions", "Schools", "Trainings",
              "TrainingFields", "TrainingTypes" TO zeka_adminarea_runtime;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO zeka_adminarea_runtime;
            REVOKE ALL ON FUNCTION public.zeka_city_key(text), public.zeka_city_text(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.zeka_city_key(text), public.zeka_city_text(text),
              zeka.current_organisation_id() TO zeka_adminarea_runtime;
            RESET ROLE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("RLS rollback requires an explicitly approved incident procedure.");
}
