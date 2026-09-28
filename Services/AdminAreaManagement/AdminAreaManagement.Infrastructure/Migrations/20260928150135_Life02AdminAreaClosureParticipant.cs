using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Life02AdminAreaClosureParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_adminarea_owner;");
            migrationBuilder.CreateTable(
                name: "AdminAreaClosureFences",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    FenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaClosureFences", x => new { x.OperationId, x.OrganisationId, x.ParticipantId });
                });

            migrationBuilder.CreateTable(
                name: "AdminAreaClosureInbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    CommandType = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaClosureInbox", x => new { x.MessageId, x.OrganisationId });
                });

            migrationBuilder.CreateTable(
                name: "AdminAreaClosureOutbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaClosureOutbox", x => new { x.MessageId, x.OrganisationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreaClosureFences_OrganisationId_ParticipantId",
                table: "AdminAreaClosureFences",
                columns: new[] { "OrganisationId", "ParticipantId" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreaClosureFences_OrganisationId_ParticipantId_FenceTo~",
                table: "AdminAreaClosureFences",
                columns: new[] { "OrganisationId", "ParticipantId", "FenceToken" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zeka.reject_adminarea_write_during_closure() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER
                SET search_path = pg_catalog
                AS $function$
                DECLARE organisation uuid;
                BEGIN
                  organisation := CASE WHEN TG_OP = 'DELETE' THEN OLD."OrganisationId" ELSE NEW."OrganisationId" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(organisation::text, 4602));
                  IF EXISTS (
                    SELECT 1 FROM public."AdminAreaClosureFences" f
                    WHERE f."OrganisationId" = organisation AND f."ReleasedAt" IS NULL
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                      MESSAGE = 'Organisation writes are blocked by an active closure fence.';
                  END IF;
                  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                REVOKE ALL ON FUNCTION zeka.reject_adminarea_write_during_closure() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.reject_adminarea_write_during_closure() TO zeka_adminarea_runtime;

                CREATE OR REPLACE FUNCTION zeka.release_adminarea_closure_fence(
                  p_operation_id uuid, p_organisation_id uuid, p_participant_id text,
                  p_operation_revision bigint, p_fence_token text, p_contract_version integer,
                  p_message_id uuid, p_causation_id uuid, p_correlation_id uuid,
                  p_released_at timestamp with time zone) RETURNS timestamp with time zone
                LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog
                AS $release_function$
                DECLARE fence public."AdminAreaClosureFences"%ROWTYPE;
                        accepted_message uuid;
                        accepted_correlation uuid;
                        identity_bytes bytea;
                        expected_message uuid;
                        effective_release timestamp with time zone;
                BEGIN
                  IF zeka.current_organisation_id() <> p_organisation_id THEN
                    RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='Closure release tenant mismatch.';
                  END IF;
                  SELECT * INTO fence FROM public."AdminAreaClosureFences"
                  WHERE "OperationId"=p_operation_id AND "OrganisationId"=p_organisation_id
                    AND "ParticipantId"=p_participant_id FOR UPDATE;
                  IF NOT FOUND OR fence."ReleasedAt" IS NOT NULL
                     OR p_operation_revision <> fence."OperationRevision" + 1
                     OR p_fence_token <> fence."FenceToken"
                     OR p_contract_version <> fence."ContractVersion" THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='Closure release does not match the active fence.';
                  END IF;
                  SELECT "MessageId", ("PayloadJson"::jsonb #>> '{header,correlationId}')::uuid
                    INTO accepted_message, accepted_correlation
                  FROM public."AdminAreaClosureOutbox"
                  WHERE "OperationId"=p_operation_id AND "OrganisationId"=p_organisation_id
                    AND "ParticipantId"=p_participant_id
                    AND "MessageType"='Zeka.Lifecycle.Contracts.OrganisationClosureParticipantCompletedV1'
                  ORDER BY "OccurredAt", "MessageId" LIMIT 1;
                  IF accepted_message IS NULL OR p_causation_id <> accepted_message
                     OR p_correlation_id <> accepted_correlation THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='Closure release lineage is invalid.';
                  END IF;
                  identity_bytes := pg_catalog.sha256(pg_catalog.convert_to(
                    'zeka-life02-v1' || chr(10) || p_operation_id::text || chr(10)
                    || p_participant_id || chr(10) || 'release-fence' || chr(10)
                    || p_operation_revision::text, 'UTF8'));
                  identity_bytes := pg_catalog.set_byte(identity_bytes, 7,
                    (pg_catalog.get_byte(identity_bytes, 7) & 15) | 80);
                  identity_bytes := pg_catalog.set_byte(identity_bytes, 8,
                    (pg_catalog.get_byte(identity_bytes, 8) & 63) | 128);
                  expected_message := (
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,4,1),'hex') ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,3,1),'hex') ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,2,1),'hex') ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,1,1),'hex') || '-' ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,6,1),'hex') ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,5,1),'hex') || '-' ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,8,1),'hex') ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,7,1),'hex') || '-' ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,9,2),'hex') || '-' ||
                    pg_catalog.encode(pg_catalog.substring(identity_bytes,11,6),'hex'))::uuid;
                  IF p_message_id <> expected_message THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='Closure release message identity is invalid.';
                  END IF;
                  effective_release := greatest(p_released_at, fence."EnteredAt");
                  UPDATE public."AdminAreaClosureFences" SET "ReleasedAt"=effective_release
                  WHERE "OperationId"=p_operation_id AND "OrganisationId"=p_organisation_id
                    AND "ParticipantId"=p_participant_id;
                  RETURN effective_release;
                END
                $release_function$;
                REVOKE ALL ON FUNCTION zeka.release_adminarea_closure_fence(
                  uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.release_adminarea_closure_fence(
                  uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)
                  TO zeka_adminarea_runtime;
                """);

            foreach (var table in new[]
                     { "Teams", "StaffMembers", "Partners", "ContactPersons", "Emails", "DocumentPartners", "StaffProjectionOutbox" })
            {
                migrationBuilder.Sql($$"""
                    DROP TRIGGER IF EXISTS trg_closure_fence_{{table.ToLowerInvariant()}} ON "{{table}}";
                    CREATE TRIGGER trg_closure_fence_{{table.ToLowerInvariant()}}
                      BEFORE INSERT OR UPDATE OR DELETE ON "{{table}}"
                      FOR EACH ROW EXECUTE FUNCTION zeka.reject_adminarea_write_during_closure();
                    """);
            }
            foreach (var table in new[]
                     { "AdminAreaClosureFences", "AdminAreaClosureInbox", "AdminAreaClosureOutbox" })
            {
                var policy = $"rls_{table.ToLowerInvariant()}_organisation";
                migrationBuilder.Sql($$"""
                    ALTER TABLE "{{table}}" OWNER TO zeka_adminarea_owner;
                    ALTER TABLE "{{table}}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{{table}}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY {{policy}} ON "{{table}}" FOR ALL TO zeka_adminarea_runtime{{(table is "AdminAreaClosureFences" or "AdminAreaClosureOutbox" ? ", zeka_adminarea_owner" : string.Empty)}}
                      USING ("OrganisationId" = zeka.current_organisation_id())
                      WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                    REVOKE ALL ON TABLE "{{table}}" FROM PUBLIC;
                    GRANT SELECT, INSERT ON TABLE "{{table}}" TO zeka_adminarea_runtime;
                    """);
            }
            migrationBuilder.Sql("RESET ROLE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Closure fence security rollback requires an explicitly approved incident procedure.");
        }
    }
}
