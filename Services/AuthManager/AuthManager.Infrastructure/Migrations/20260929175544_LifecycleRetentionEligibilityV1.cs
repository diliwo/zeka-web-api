using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleRetentionEligibilityV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");

            migrationBuilder.AddColumn<string>(
                name: "DispositionInventoryHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispositionReadyAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetentionDecisionSetHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // This owner-only, transactional conversion preserves the old archive timestamp and
            // fence hash as closure-phase truth. NO FORCE permits only the table owner to inspect
            // existing rows during the migration; runtime RLS stays enabled throughout.
            migrationBuilder.Sql("""
                ALTER TABLE public."OrganisationLifecycleOperations" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."OrganisationLifecycleParticipants" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecycleClosureFenceReceipts" NO FORCE ROW LEVEL SECURITY;
                DO $conversion$
                BEGIN
                  IF EXISTS (
                    SELECT 1 FROM public."OrganisationLifecycleOperations" op
                    LEFT JOIN public."Organisations" org ON org."Id"=op."OrganisationId"
                    WHERE op."Family"=1 AND op."State"=6
                      AND (op."RegistryRevision"<>'46020000-0000-0000-0000-000000000002'::uuid
                        OR op."ArchivedAt" IS NULL OR op."ClosingAt" IS NULL
                        OR op."ClosureFenceEvidenceHash" IS NULL
                        OR op."CompletedAt" IS DISTINCT FROM op."ArchivedAt"
                        OR op."IsActive" OR org."Status"<>6
                        OR (SELECT count(*) FROM public."OrganisationLifecycleParticipants" p
                            WHERE p."OperationId"=op."Id" AND p."Family"=1
                              AND p."CapabilityKey"='organisation.closure-fence'
                              AND p."Mandatory" AND p."State"=2)<>4
                        OR (SELECT count(*) FROM public."LifecycleClosureFenceReceipts" receipt
                            WHERE receipt."OperationId"=op."Id")<>4)
                  ) THEN
                    RAISE EXCEPTION 'Cannot truthfully convert a legacy archived termination operation.';
                  END IF;
                  IF EXISTS (
                    SELECT 1 FROM public."OrganisationLifecycleOperations" op
                    WHERE op."Family"=1 AND op."State"=6
                      AND EXISTS (SELECT 1 FROM public."OrganisationLifecycleOperations" other
                        WHERE other."OrganisationId"=op."OrganisationId"
                          AND other."Family"=1 AND other."Id"<>op."Id"
                          AND (other."IsActive" OR other."State"=6))
                  ) THEN
                    RAISE EXCEPTION 'Legacy archive conversion would violate active-family uniqueness.';
                  END IF;
                END $conversion$;
                UPDATE public."OrganisationLifecycleOperations"
                   SET "State"=8, "IsActive"=true, "CompletedAt"=NULL,
                       "Revision"="Revision"+1
                 WHERE "Family"=1 AND "State"=6;
                ALTER TABLE public."LifecycleClosureFenceReceipts" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."OrganisationLifecycleParticipants" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."OrganisationLifecycleOperations" FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.CreateTable(
                name: "RetentionDecisionSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    RegistryRevision = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DispositionInventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ready = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionDecisionSets", x => x.Id);
                    table.UniqueConstraint("AK_RetentionDecisionSets_Id_OperationId_OrganisationId", x => new { x.Id, x.OperationId, x.OrganisationId });
                    table.ForeignKey(
                        name: "FK_RetentionDecisionSets_OrganisationLifecycleOperations_Opera~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetentionDecisionRecords",
                columns: table => new
                {
                    SetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    EligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HoldReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReasonCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionDecisionRecords", x => new { x.SetId, x.Category });
                    table.ForeignKey(
                        name: "FK_RetentionDecisionRecords_RetentionDecisionSets_SetId_Operat~",
                        columns: x => new { x.SetId, x.OperationId, x.OrganisationId },
                        principalTable: "RetentionDecisionSets",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionDecisionRecords_SetId_OperationId_OrganisationId",
                table: "RetentionDecisionRecords",
                columns: new[] { "SetId", "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionDecisionSets_OperationId_EvaluatedAt",
                table: "RetentionDecisionSets",
                columns: new[] { "OperationId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionDecisionSets_OperationId_OrganisationId",
                table: "RetentionDecisionSets",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.Sql("""
                ALTER TABLE public."RetentionDecisionSets" OWNER TO zeka_auth_owner;
                ALTER TABLE public."RetentionDecisionRecords" OWNER TO zeka_auth_owner;
                ALTER TABLE public."RetentionDecisionSets" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RetentionDecisionSets" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_retentiondecisionsets_organisation
                  ON public."RetentionDecisionSets" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                ALTER TABLE public."RetentionDecisionRecords" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RetentionDecisionRecords" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_retentiondecisionrecords_organisation
                  ON public."RetentionDecisionRecords" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE public."RetentionDecisionSets",
                  public."RetentionDecisionRecords" FROM PUBLIC, zeka_auth_runtime;
                GRANT SELECT, INSERT ON TABLE public."RetentionDecisionSets",
                  public."RetentionDecisionRecords" TO zeka_auth_runtime;
                GRANT UPDATE ("DispositionReadyAt", "RetentionDecisionSetHash")
                  ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
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
                      AND (execution."State"=1 OR execution."State"=2 AND OLD."Status"=5)
                  ) THEN
                    IF TG_OP<>'UPDATE'
                       OR NOT (OLD."Status"=5 AND NEW."Status" IN (2,6)
                           OR OLD."Status"=6 AND NEW."Status"=7)
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
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Retention evidence removal requires a separately reviewed migration.");
        }
    }
}
