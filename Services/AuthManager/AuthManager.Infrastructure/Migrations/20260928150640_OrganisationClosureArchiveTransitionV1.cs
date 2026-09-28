using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationClosureArchiveTransitionV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedAt",
                table: "OrganisationLifecycleParticipants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "OrganisationLifecycleParticipants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FailureRetryable",
                table: "OrganisationLifecycleParticipants",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureBoundaryDisposition",
                table: "OrganisationLifecycleParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosingAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosureFenceEvidenceHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            foreach (var (table, columns, primaryKey, foreignKey) in new[]
                     {
                         (
                             "AuthClosureParticipantExecutions",
                             """
                             "OperationId" uuid NOT NULL,
                             "OrganisationId" uuid NOT NULL,
                             "OperationRevision" bigint NOT NULL,
                             "FenceToken" character varying(200) NOT NULL,
                             "FenceRevision" bigint NOT NULL,
                             "BoundaryEstablishedAt" timestamp with time zone NOT NULL,
                             "ReceiptHash" character varying(64) NOT NULL,
                             "ReleasedAt" timestamp with time zone NULL,
                             "State" integer NOT NULL
                             """,
                             "\"OperationId\"",
                             "FK_AuthClosureParticipantExecutions_OrganisationLifecycleOpera~"
                         ),
                         (
                             "AuthClosureParticipantInbox",
                             """
                             "MessageId" uuid NOT NULL,
                             "OperationId" uuid NOT NULL,
                             "OrganisationId" uuid NOT NULL,
                             "MessageType" character varying(300) NOT NULL,
                             "PayloadSha256" character varying(64) NOT NULL,
                             "ReceivedAt" timestamp with time zone NOT NULL
                             """,
                             "\"MessageId\"",
                             "FK_AuthClosureParticipantInbox_OrganisationLifecycleOperations~"
                         ),
                         (
                             "AuthClosureParticipantOutbox",
                             """
                             "MessageId" uuid NOT NULL,
                             "OperationId" uuid NOT NULL,
                             "OrganisationId" uuid NOT NULL,
                             "MessageType" character varying(300) NOT NULL,
                             "PayloadJson" jsonb NOT NULL,
                             "PayloadSha256" character varying(64) NOT NULL,
                             "OccurredAt" timestamp with time zone NOT NULL
                             """,
                             "\"MessageId\"",
                             "FK_AuthClosureParticipantOutbox_OrganisationLifecycleOperation~"
                         ),
                         (
                             "LifecycleClosureFenceReceipts",
                             """
                             "OperationId" uuid NOT NULL,
                             "ParticipantId" character varying(200) NOT NULL,
                             "OrganisationId" uuid NOT NULL,
                             "ContractVersion" integer NOT NULL,
                             "OperationRevision" bigint NOT NULL,
                             "FenceToken" character varying(200) NOT NULL,
                             "FenceRevision" bigint NOT NULL,
                             "BoundaryEstablishedAt" timestamp with time zone NOT NULL,
                             "ReceiptHash" character varying(64) NOT NULL,
                             "MessageId" uuid NOT NULL,
                             "CausationId" uuid NOT NULL,
                             "CorrelationId" uuid NOT NULL
                             """,
                             "\"OperationId\", \"ParticipantId\"",
                             "FK_LifecycleClosureFenceReceipts_OrganisationLifecycleOperatio~"
                         )
                     })
            {
                CreateOperationScopedTable(migrationBuilder, table, columns, primaryKey, foreignKey);
            }

            foreach (var (name, table, columns, unique) in new[]
                     {
                         ("IX_AuthClosureParticipantExecutions_OperationId_OrganisationId",
                             "AuthClosureParticipantExecutions", new[] { "OperationId", "OrganisationId" }, false),
                         ("IX_AuthClosureParticipantInbox_OperationId_MessageType",
                             "AuthClosureParticipantInbox", new[] { "OperationId", "MessageType" }, false),
                         ("IX_AuthClosureParticipantInbox_OperationId_OrganisationId",
                             "AuthClosureParticipantInbox", new[] { "OperationId", "OrganisationId" }, false),
                         ("IX_AuthClosureParticipantOutbox_OperationId_MessageType",
                             "AuthClosureParticipantOutbox", new[] { "OperationId", "MessageType" }, true),
                         ("IX_AuthClosureParticipantOutbox_OperationId_OrganisationId",
                             "AuthClosureParticipantOutbox", new[] { "OperationId", "OrganisationId" }, false),
                         ("IX_LifecycleClosureFenceReceipts_OperationId_OrganisationId",
                             "LifecycleClosureFenceReceipts", new[] { "OperationId", "OrganisationId" }, false)
                     })
            {
                migrationBuilder.CreateIndex(name: name, table: table, columns: columns, unique: unique);
            }

            foreach (var table in new[]
                     {
                         "AuthClosureParticipantExecutions", "AuthClosureParticipantInbox",
                         "AuthClosureParticipantOutbox", "LifecycleClosureFenceReceipts"
                     })
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
                ALTER POLICY rls_authclosureparticipantexecutions_organisation
                  ON "AuthClosureParticipantExecutions" TO zeka_auth_runtime, zeka_auth_owner;
                ALTER POLICY rls_authclosureparticipantoutbox_organisation
                  ON "AuthClosureParticipantOutbox" TO zeka_auth_runtime, zeka_auth_owner;
                GRANT UPDATE ("FailureBoundaryDisposition", "FailureCode", "FailureRetryable", "FailedAt", "State")
                  ON TABLE "OrganisationLifecycleParticipants" TO zeka_auth_runtime;
                GRANT UPDATE ("ClosingAt", "ArchivedAt", "ClosureFenceEvidenceHash")
                  ON TABLE "OrganisationLifecycleOperations" TO zeka_auth_runtime;

                CREATE OR REPLACE FUNCTION zeka.reject_auth_owned_write_during_closure_fence()
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
                    SELECT 1 FROM public."AuthClosureParticipantExecutions" execution
                    WHERE execution."OrganisationId"=scoped_organisation
                      AND execution."State"=1
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE='55000',
                      MESSAGE='organisation closure fence blocks Auth ordinary mutation';
                  END IF;
                  RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                ALTER FUNCTION zeka.reject_auth_owned_write_during_closure_fence() OWNER TO zeka_auth_owner;
                REVOKE ALL ON FUNCTION zeka.reject_auth_owned_write_during_closure_fence()
                  FROM PUBLIC, zeka_auth_runtime;
                GRANT EXECUTE ON FUNCTION zeka.reject_auth_owned_write_during_closure_fence()
                  TO zeka_auth_runtime;

                CREATE OR REPLACE FUNCTION zeka.reject_auth_organisation_write_during_closure_fence()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY INVOKER
                SET search_path = pg_catalog, public, zeka
                AS $function$
                DECLARE scoped_organisation uuid;
                BEGIN
                  scoped_organisation := CASE WHEN TG_OP='DELETE' THEN OLD."Id" ELSE NEW."Id" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(scoped_organisation::text, 0));
                  IF EXISTS (
                    SELECT 1 FROM public."AuthClosureParticipantExecutions" execution
                    WHERE execution."OrganisationId"=scoped_organisation
                      AND execution."State"=1
                  ) THEN
                    IF TG_OP<>'UPDATE'
                       OR OLD."Status"<>5 OR NEW."Status" NOT IN (2,6)
                       OR (pg_catalog.to_jsonb(NEW) - ARRAY['Status','UpdatedAtUtc','ConcurrencyVersion'])
                          IS DISTINCT FROM
                          (pg_catalog.to_jsonb(OLD) - ARRAY['Status','UpdatedAtUtc','ConcurrencyVersion']) THEN
                      RAISE EXCEPTION USING ERRCODE='55000',
                        MESSAGE='organisation closure fence blocks Auth organisation mutation';
                    END IF;
                  END IF;
                  RETURN CASE WHEN TG_OP='DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                ALTER FUNCTION zeka.reject_auth_organisation_write_during_closure_fence() OWNER TO zeka_auth_owner;
                REVOKE ALL ON FUNCTION zeka.reject_auth_organisation_write_during_closure_fence()
                  FROM PUBLIC, zeka_auth_runtime;
                GRANT EXECUTE ON FUNCTION zeka.reject_auth_organisation_write_during_closure_fence()
                  TO zeka_auth_runtime;

                CREATE OR REPLACE FUNCTION zeka.release_auth_closure_fence(
                  p_operation_id uuid, p_organisation_id uuid, p_revision bigint,
                  p_fence_token text, p_causation_id uuid, p_correlation_id uuid,
                  p_released_at timestamptz)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                  IF p_released_at IS NULL THEN
                    RAISE EXCEPTION USING ERRCODE='22004', MESSAGE='closure release timestamp is required';
                  END IF;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(p_organisation_id::text, 0));
                  IF p_organisation_id <> zeka.current_organisation_id()
                     OR p_correlation_id <> p_operation_id THEN
                    RAISE EXCEPTION USING ERRCODE='42501', MESSAGE='closure release identity rejected';
                  END IF;
                  IF NOT EXISTS (
                    SELECT 1
                    FROM public."AuthClosureParticipantExecutions" execution
                    JOIN public."Organisations" organisation
                      ON organisation."Id"=execution."OrganisationId"
                    JOIN public."AuthClosureParticipantOutbox" entered
                      ON entered."OperationId"=execution."OperationId"
                     AND entered."MessageType"='OrganisationClosureParticipantCompletedV1'
                    WHERE execution."OperationId"=p_operation_id
                      AND execution."OrganisationId"=p_organisation_id
                      AND execution."State"=1
                      AND execution."OperationRevision"+1=p_revision
                      AND execution."FenceToken"=p_fence_token
                      AND p_released_at>=execution."BoundaryEstablishedAt"
                      AND organisation."Status"=5
                      AND (entered."PayloadJson"->'header'->>'messageId')::uuid=p_causation_id
                      AND (entered."PayloadJson"->'header'->>'correlationId')::uuid=p_operation_id
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE='55000', MESSAGE='closure release durable provenance rejected';
                  END IF;
                  UPDATE public."AuthClosureParticipantExecutions"
                     SET "State"=2, "ReleasedAt"=p_released_at
                   WHERE "OperationId"=p_operation_id AND "OrganisationId"=p_organisation_id;
                  RETURN FOUND;
                END
                $function$;
                ALTER FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
                  OWNER TO zeka_auth_owner;
                REVOKE ALL ON FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
                  FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
                  TO zeka_auth_runtime;

                CREATE TRIGGER trg_auth_membership_closure_fence
                  BEFORE INSERT OR UPDATE OR DELETE ON public."OrganisationMemberships"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_auth_owned_write_during_closure_fence();
                CREATE TRIGGER trg_auth_permission_grant_closure_fence
                  BEFORE INSERT OR UPDATE OR DELETE ON public."MembershipPermissionGrants"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_auth_owned_write_during_closure_fence();
                CREATE TRIGGER trg_auth_organisation_closure_fence
                  BEFORE UPDATE OR DELETE ON public."Organisations"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_auth_organisation_write_during_closure_fence();
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_auth_membership_closure_fence ON public."OrganisationMemberships";
                DROP TRIGGER IF EXISTS trg_auth_permission_grant_closure_fence ON public."MembershipPermissionGrants";
                DROP TRIGGER IF EXISTS trg_auth_organisation_closure_fence ON public."Organisations";
                DROP FUNCTION IF EXISTS zeka.reject_auth_owned_write_during_closure_fence();
                DROP FUNCTION IF EXISTS zeka.reject_auth_organisation_write_during_closure_fence();
                DROP FUNCTION IF EXISTS zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz);
                """);
            foreach (var table in new[]
                     {
                         "AuthClosureParticipantExecutions", "AuthClosureParticipantInbox",
                         "AuthClosureParticipantOutbox", "LifecycleClosureFenceReceipts"
                     })
            {
                migrationBuilder.DropTable(name: table);
            }

            migrationBuilder.DropColumn(
                name: "FailedAt",
                table: "OrganisationLifecycleParticipants");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "OrganisationLifecycleParticipants");

            migrationBuilder.DropColumn(
                name: "FailureRetryable",
                table: "OrganisationLifecycleParticipants");

            migrationBuilder.DropColumn(
                name: "FailureBoundaryDisposition",
                table: "OrganisationLifecycleParticipants");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "OrganisationLifecycleOperations");

            migrationBuilder.DropColumn(
                name: "ClosingAt",
                table: "OrganisationLifecycleOperations");

            migrationBuilder.DropColumn(
                name: "ClosureFenceEvidenceHash",
                table: "OrganisationLifecycleOperations");

            migrationBuilder.Sql("RESET ROLE;");
        }

        private static void CreateOperationScopedTable(
            MigrationBuilder migrationBuilder,
            string table,
            string columns,
            string primaryKey,
            string foreignKey)
        {
            migrationBuilder.Sql($$"""
                CREATE TABLE "{{table}}" (
                  {{columns}},
                  CONSTRAINT "PK_{{table}}" PRIMARY KEY ({{primaryKey}}),
                  CONSTRAINT "{{foreignKey}}" FOREIGN KEY ("OperationId", "OrganisationId")
                    REFERENCES "OrganisationLifecycleOperations" ("Id", "OrganisationId") ON DELETE RESTRICT
                );
                """);
        }
    }
}
