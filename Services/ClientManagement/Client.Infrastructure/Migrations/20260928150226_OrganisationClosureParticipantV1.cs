using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationClosureParticipantV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_client_owner;");
            migrationBuilder.CreateTable(
                name: "OrganisationClosureFences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationClosureFences", x => x.Id);
                    table.UniqueConstraint("AK_OrganisationClosureFences_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                });

            migrationBuilder.CreateTable(
                name: "OrganisationClosureInbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResultMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationClosureInbox", x => x.Id);
                    table.UniqueConstraint("AK_OrganisationClosureInbox_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                });

            migrationBuilder.CreateTable(
                name: "OrganisationClosureOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationClosureOutbox", x => x.Id);
                    table.UniqueConstraint("AK_OrganisationClosureOutbox_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationClosureFences_OperationId_OrganisationId",
                table: "OrganisationClosureFences",
                columns: new[] { "OperationId", "OrganisationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationClosureFences_OrganisationId",
                table: "OrganisationClosureFences",
                column: "OrganisationId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationClosureInbox_OrganisationId_MessageId",
                table: "OrganisationClosureInbox",
                columns: new[] { "OrganisationId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationClosureOutbox_OrganisationId_MessageId",
                table: "OrganisationClosureOutbox",
                columns: new[] { "OrganisationId", "MessageId" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zeka.reject_writes_during_closure_fence() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $function$
                DECLARE organisation uuid;
                BEGIN
                  IF TG_OP = 'DELETE' THEN organisation := OLD."OrganisationId";
                  ELSE organisation := NEW."OrganisationId"; END IF;
                  IF organisation IS NULL OR organisation = '00000000-0000-0000-0000-000000000000'::uuid
                    OR organisation <> zeka.current_organisation_id() THEN
                    RAISE EXCEPTION 'Tenant database context is invalid.';
                  END IF;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(pg_catalog.lower(organisation::text), 0));
                  IF EXISTS (SELECT 1 FROM public."OrganisationClosureFences"
                    WHERE "OrganisationId" = organisation AND "ReleasedAt" IS NULL) THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'organisation_closure_fence_active';
                  END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $function$;
                REVOKE ALL ON FUNCTION zeka.reject_writes_during_closure_fence() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.reject_writes_during_closure_fence() TO zeka_client_runtime;

                CREATE OR REPLACE FUNCTION zeka.release_organisation_closure_fence(
                  p_organisation_id uuid,
                  p_operation_id uuid,
                  p_participant_id text,
                  p_operation_revision bigint,
                  p_fence_token text,
                  p_contract_version integer,
                  p_release_message_id uuid,
                  p_causation_id uuid,
                  p_correlation_id uuid,
                  p_released_at timestamp with time zone) RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $function$
                DECLARE
                  persisted_revision bigint;
                  persisted_entered_at timestamp with time zone;
                  persisted_released_at timestamp with time zone;
                  persisted_fence_token text;
                  identity_bytes bytea;
                  expected_message_id uuid;
                BEGIN
                  IF p_organisation_id IS NULL
                    OR p_organisation_id <> zeka.current_organisation_id()
                    OR p_operation_id IS NULL
                    OR p_participant_id <> 'client-management'
                    OR p_contract_version <> 1
                    OR p_operation_revision <= 0
                    OR p_release_message_id IS NULL
                    OR p_causation_id IS NULL
                    OR p_correlation_id IS NULL
                    OR p_released_at IS NULL THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_release_identity_invalid';
                  END IF;

                  identity_bytes := pg_catalog.substring(pg_catalog.sha256(pg_catalog.convert_to(
                    'zeka-life02-v1' || pg_catalog.chr(10)
                    || p_operation_id::text || pg_catalog.chr(10)
                    || p_participant_id || pg_catalog.chr(10)
                    || 'release-fence' || pg_catalog.chr(10)
                    || p_operation_revision::text, 'UTF8')), 1, 16);
                  identity_bytes := pg_catalog.set_byte(identity_bytes, 7,
                    (pg_catalog.get_byte(identity_bytes, 7) & 15) | 80);
                  identity_bytes := pg_catalog.set_byte(identity_bytes, 8,
                    (pg_catalog.get_byte(identity_bytes, 8) & 63) | 128);
                  expected_message_id := pg_catalog.encode(
                    pg_catalog.substring(identity_bytes, 4, 1)
                    || pg_catalog.substring(identity_bytes, 3, 1)
                    || pg_catalog.substring(identity_bytes, 2, 1)
                    || pg_catalog.substring(identity_bytes, 1, 1)
                    || pg_catalog.substring(identity_bytes, 6, 1)
                    || pg_catalog.substring(identity_bytes, 5, 1)
                    || pg_catalog.substring(identity_bytes, 8, 1)
                    || pg_catalog.substring(identity_bytes, 7, 1)
                    || pg_catalog.substring(identity_bytes, 9, 8), 'hex')::uuid;
                  IF p_release_message_id <> expected_message_id THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_release_message_invalid';
                  END IF;

                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(pg_catalog.lower(p_organisation_id::text), 0));
                  SELECT fence."OperationRevision", fence."EnteredAt", fence."ReleasedAt", fence."FenceToken"
                  INTO persisted_revision, persisted_entered_at, persisted_released_at, persisted_fence_token
                  FROM public."OrganisationClosureFences" fence
                  WHERE fence."OrganisationId" = p_organisation_id
                    AND fence."OperationId" = p_operation_id
                    AND fence."ParticipantId" = p_participant_id
                  FOR UPDATE;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_fence_release_conflict';
                  END IF;
                  IF p_fence_token <> persisted_fence_token THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_fence_token_invalid';
                  END IF;
                  IF p_operation_revision <> persisted_revision + 1 THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_revision_conflict';
                  END IF;
                  IF NOT EXISTS (
                    SELECT 1 FROM public."OrganisationClosureInbox" receipt
                    WHERE receipt."OrganisationId" = p_organisation_id
                      AND receipt."OperationId" = p_operation_id
                      AND receipt."OperationRevision" = persisted_revision
                      AND receipt."ResultMessageId" = p_causation_id
                      AND receipt."ResponseType" =
                        'Zeka.Lifecycle.Contracts.OrganisationClosureParticipantCompletedV1'
                      AND (receipt."ResponseJson"::jsonb #>> '{header,messageId}')::uuid = p_causation_id
                      AND (receipt."ResponseJson"::jsonb #>> '{header,correlationId}')::uuid = p_correlation_id
                      AND (receipt."ResponseJson"::jsonb #>> '{header,participantId}') = p_participant_id
                      AND (receipt."ResponseJson"::jsonb #>> '{header,contractVersion}')::integer = p_contract_version) THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_recovery_identity_conflict';
                  END IF;

                  IF persisted_released_at IS NOT NULL THEN
                    RETURN true;
                  END IF;

                  UPDATE public."OrganisationClosureFences"
                  SET "ReleasedAt" = greatest(p_released_at, persisted_entered_at)
                  WHERE "OrganisationId" = p_organisation_id
                    AND "OperationId" = p_operation_id
                    AND "FenceToken" = p_fence_token
                    AND "ReleasedAt" IS NULL;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                      MESSAGE = 'organisation_closure_fence_release_conflict';
                  END IF;
                  RETURN true;
                END $function$;
                REVOKE ALL ON FUNCTION zeka.release_organisation_closure_fence(
                  uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)
                  FROM PUBLIC, zeka_client_runtime;
                GRANT EXECUTE ON FUNCTION zeka.release_organisation_closure_fence(
                  uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)
                  TO zeka_client_closure_recovery;

                ALTER TABLE "OrganisationClosureFences" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "OrganisationClosureFences" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "OrganisationClosureInbox" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "OrganisationClosureInbox" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "OrganisationClosureOutbox" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "OrganisationClosureOutbox" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_organisationclosurefences_organisation ON "OrganisationClosureFences"
                  FOR ALL TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_organisationclosureinbox_organisation ON "OrganisationClosureInbox"
                  FOR ALL TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_organisationclosureoutbox_organisation ON "OrganisationClosureOutbox"
                  FOR ALL TO zeka_client_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());

                REVOKE ALL ON TABLE "OrganisationClosureFences", "OrganisationClosureInbox",
                  "OrganisationClosureOutbox" FROM PUBLIC;
                GRANT SELECT, INSERT ON TABLE "OrganisationClosureFences", "OrganisationClosureInbox",
                  "OrganisationClosureOutbox" TO zeka_client_runtime;
                GRANT USAGE, SELECT ON SEQUENCE "OrganisationClosureFences_Id_seq",
                  "OrganisationClosureInbox_Id_seq", "OrganisationClosureOutbox_Id_seq" TO zeka_client_runtime;

                CREATE TRIGGER trg_clients_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "Clients"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_socialworkers_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "SocialWorkers"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_socialcases_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "SocialCases"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_assessments_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "Assessments"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_professionalassessments_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "ProfessionalAssessments"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_professionnalexperience_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "ProfessionnalExperience"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_schoolregistrations_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "SchoolRegistrations"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_monitoringreports_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "MonitoringReports"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException(
                "Closure fence rollback requires an explicitly approved incident procedure.");
    }
}
