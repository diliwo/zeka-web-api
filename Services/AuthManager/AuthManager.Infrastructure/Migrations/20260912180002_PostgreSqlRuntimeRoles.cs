using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations;

[DbContext(typeof(AuthDbContext))]
[Migration("20260912180002_PostgreSqlRuntimeRoles")]
public sealed class PostgreSqlRuntimeRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $roles$
        BEGIN
          IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_auth_owner')
             OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_auth_migrator')
             OR NOT EXISTS (SELECT FROM pg_roles WHERE rolname='zeka_auth_runtime') THEN
            RAISE EXCEPTION 'Required AuthManagement database roles are not provisioned.';
          END IF;
        END $roles$;
        SET ROLE zeka_auth_owner;
        REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM zeka_auth_runtime;
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE "__EFMigrationsHistory" TO zeka_auth_migrator;
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
          "Organisations", "OrganisationMemberships", "AuditEntries", "OutboxMessages", "AspNetUsers",
          "AspNetRoles", "AspNetRoleClaims", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles",
          "AspNetUserTokens", "PermissionSets", "IdempotencyRecords" TO zeka_auth_runtime;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO zeka_auth_runtime;
        RESET ROLE;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Runtime-role rollback requires an explicitly approved incident procedure.");
}
