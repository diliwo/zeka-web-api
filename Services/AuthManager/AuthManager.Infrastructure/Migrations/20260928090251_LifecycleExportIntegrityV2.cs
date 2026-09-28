using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleExportIntegrityV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");

            migrationBuilder.AddColumn<string>(
                name: "ExportInventoryHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExportInventoryJson",
                table: "OrganisationLifecycleOperations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuthExportParticipantExecutions",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    FenceEnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FenceReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FenceEvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FragmentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CategoriesJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthExportParticipantExecutions", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_AuthExportParticipantExecutions_OrganisationLifecycleOperat~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthExportParticipantInbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthExportParticipantInbox", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_AuthExportParticipantInbox_OrganisationLifecycleOperations_~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuthExportParticipantOutbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthExportParticipantOutbox", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_AuthExportParticipantOutbox_OrganisationLifecycleOperations~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthExportParticipantExecutions_OperationId_OrganisationId",
                table: "AuthExportParticipantExecutions",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthExportParticipantInbox_OperationId_MessageType",
                table: "AuthExportParticipantInbox",
                columns: new[] { "OperationId", "MessageType" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthExportParticipantInbox_OperationId_OrganisationId",
                table: "AuthExportParticipantInbox",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthExportParticipantOutbox_OperationId_MessageType",
                table: "AuthExportParticipantOutbox",
                columns: new[] { "OperationId", "MessageType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuthExportParticipantOutbox_OperationId_OrganisationId",
                table: "AuthExportParticipantOutbox",
                columns: new[] { "OperationId", "OrganisationId" });

            foreach (var table in new[]
                     { "AuthExportParticipantExecutions", "AuthExportParticipantInbox", "AuthExportParticipantOutbox" })
            {
                var policy = $"rls_{table.ToLowerInvariant()}_organisation";
                migrationBuilder.Sql($$"""
                    ALTER TABLE "{{table}}" OWNER TO zeka_auth_owner;
                    ALTER TABLE "{{table}}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{{table}}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY {{policy}} ON "{{table}}" FOR ALL TO zeka_auth_runtime
                      USING ("OrganisationId" = zeka.current_organisation_id())
                      WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                    REVOKE ALL ON TABLE "{{table}}" FROM PUBLIC, zeka_auth_runtime;
                    GRANT SELECT, INSERT ON TABLE "{{table}}" TO zeka_auth_runtime;
                    """);
            }

            migrationBuilder.Sql("""
                GRANT UPDATE ("SnapshotAt", "FenceEvidenceHash", "FragmentHash", "CategoriesJson",
                  "ReleasedAt", "State")
                  ON TABLE "AuthExportParticipantExecutions" TO zeka_auth_runtime;

                CREATE OR REPLACE FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY INVOKER
                SET search_path = pg_catalog, public, zeka
                AS $function$
                DECLARE scoped_organisation uuid;
                BEGIN
                  scoped_organisation := CASE WHEN TG_OP='DELETE' THEN OLD."OrganisationId" ELSE NEW."OrganisationId" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(scoped_organisation::text, 0));
                  IF EXISTS (
                    SELECT 1
                    FROM public."AuthExportParticipantExecutions" execution
                    WHERE execution."OrganisationId"=scoped_organisation
                      AND execution."State"<>2
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='organisation export fence blocks membership mutation';
                  END IF;
                  RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                ALTER FUNCTION zeka.reject_auth_membership_write_during_export_fence() OWNER TO zeka_auth_owner;
                REVOKE ALL ON FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                  FROM PUBLIC, zeka_auth_runtime;
                GRANT EXECUTE ON FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                  TO zeka_auth_runtime;
                DROP TRIGGER IF EXISTS trg_auth_permission_grant_export_fence
                  ON public."MembershipPermissionGrants";
                CREATE TRIGGER trg_auth_permission_grant_export_fence
                  BEFORE INSERT OR UPDATE OR DELETE ON public."MembershipPermissionGrants"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_auth_membership_write_during_export_fence();
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY INVOKER
                SET search_path = pg_catalog, public, zeka
                AS $function$
                DECLARE scoped_organisation uuid;
                BEGIN
                  scoped_organisation := CASE WHEN TG_OP='DELETE' THEN OLD."OrganisationId" ELSE NEW."OrganisationId" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(scoped_organisation::text, 0));
                  IF EXISTS (
                    SELECT 1
                    FROM public."LifecycleExportFenceReceipts" receipt
                    JOIN public."OrganisationLifecycleOperations" operation
                      ON operation."Id"=receipt."OperationId" AND operation."OrganisationId"=receipt."OrganisationId"
                    WHERE receipt."OrganisationId"=scoped_organisation
                      AND receipt."ParticipantId"='auth-management'
                      AND operation."IsActive"
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='organisation export fence blocks membership mutation';
                  END IF;
                  RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                ALTER FUNCTION zeka.reject_auth_membership_write_during_export_fence() OWNER TO zeka_auth_owner;
                REVOKE ALL ON FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                  FROM PUBLIC, zeka_auth_runtime;
                GRANT EXECUTE ON FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                  TO zeka_auth_runtime;
                DROP TRIGGER IF EXISTS trg_auth_permission_grant_export_fence
                  ON public."MembershipPermissionGrants";
                """);

            migrationBuilder.DropTable(
                name: "AuthExportParticipantExecutions");

            migrationBuilder.DropTable(
                name: "AuthExportParticipantInbox");

            migrationBuilder.DropTable(
                name: "AuthExportParticipantOutbox");

            migrationBuilder.DropColumn(
                name: "ExportInventoryHash",
                table: "OrganisationLifecycleOperations");

            migrationBuilder.DropColumn(
                name: "ExportInventoryJson",
                table: "OrganisationLifecycleOperations");

            migrationBuilder.Sql("RESET ROLE;");
        }
    }
}
