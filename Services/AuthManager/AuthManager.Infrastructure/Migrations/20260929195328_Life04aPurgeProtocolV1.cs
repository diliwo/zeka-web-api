using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Life04aPurgeProtocolV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.AddColumn<long>(
                name: "IrreversibleRevision",
                table: "OrganisationLifecycleOperations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IrreversibleStartedAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurgeBoundaryEvidenceHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PurgeExecutionCompletedAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurgePlanHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LifecyclePurgePlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdmittedOperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    RegistryRevision = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DispositionInventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DecisionSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionSetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EntriesJson = table.Column<string>(type: "text", nullable: false),
                    PlanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecyclePurgePlans", x => x.Id);
                    table.UniqueConstraint("AK_LifecyclePurgePlans_Id_OperationId_OrganisationId", x => new { x.Id, x.OperationId, x.OrganisationId });
                    table.ForeignKey(
                        name: "FK_LifecyclePurgePlans_OrganisationLifecycleOperations_Operati~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LifecyclePurgePlans_RetentionDecisionSets_DecisionSetId_Ope~",
                        columns: x => new { x.DecisionSetId, x.OperationId, x.OrganisationId },
                        principalTable: "RetentionDecisionSets",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecyclePurgeOutbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecyclePurgeOutbox", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_LifecyclePurgeOutbox_LifecyclePurgePlans_PlanId_OperationId~",
                        columns: x => new { x.PlanId, x.OperationId, x.OrganisationId },
                        principalTable: "LifecyclePurgePlans",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecyclePurgeProgress",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommandMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SafeFailureCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecyclePurgeProgress", x => new { x.OperationId, x.Category });
                    table.ForeignKey(
                        name: "FK_LifecyclePurgeProgress_LifecyclePurgePlans_PlanId_Operation~",
                        columns: x => new { x.PlanId, x.OperationId, x.OrganisationId },
                        principalTable: "LifecyclePurgePlans",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgeOutbox_OperationId_Kind_Category",
                table: "LifecyclePurgeOutbox",
                columns: new[] { "OperationId", "Kind", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgeOutbox_PlanId_OperationId_OrganisationId",
                table: "LifecyclePurgeOutbox",
                columns: new[] { "PlanId", "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgePlans_DecisionSetId_OperationId_OrganisationId",
                table: "LifecyclePurgePlans",
                columns: new[] { "DecisionSetId", "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgePlans_OperationId",
                table: "LifecyclePurgePlans",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgePlans_OperationId_OrganisationId",
                table: "LifecyclePurgePlans",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecyclePurgeProgress_PlanId_OperationId_OrganisationId",
                table: "LifecyclePurgeProgress",
                columns: new[] { "PlanId", "OperationId", "OrganisationId" });
            migrationBuilder.Sql("""
                ALTER TABLE public."LifecyclePurgePlans" OWNER TO zeka_auth_owner;
                ALTER TABLE public."LifecyclePurgeOutbox" OWNER TO zeka_auth_owner;
                ALTER TABLE public."LifecyclePurgeProgress" OWNER TO zeka_auth_owner;
                ALTER TABLE public."LifecyclePurgePlans" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecyclePurgePlans" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecyclePurgeOutbox" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecyclePurgeOutbox" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecyclePurgeProgress" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecyclePurgeProgress" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_lifecyclepurgeplans_organisation
                  ON public."LifecyclePurgePlans" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_lifecyclepurgeoutbox_organisation
                  ON public."LifecyclePurgeOutbox" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_lifecyclepurgeprogress_organisation
                  ON public."LifecyclePurgeProgress" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE public."LifecyclePurgePlans", public."LifecyclePurgeOutbox",
                  public."LifecyclePurgeProgress" FROM PUBLIC, zeka_auth_runtime;
                -- Production runtime gets no destructive-plan grants. A restricted test-only
                -- fixture role may receive exact grants in an isolated conformance database.
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
                  IF TG_OP='UPDATE' AND OLD."Status"=7 AND NEW."Status"=8
                     AND current_user<>'zeka_auth_purge_fixture' THEN
                    RAISE EXCEPTION USING ERRCODE='55000',
                      MESSAGE='purge fixture transition is unavailable to production runtime';
                  END IF;
                  IF EXISTS (
                    SELECT 1 FROM public."AuthClosureParticipantExecutions" execution
                    WHERE execution."OrganisationId"=scoped_organisation
                      AND (execution."State"=1 OR execution."State"=2
                        AND OLD."Status" IN (5,6,7,8))
                  ) THEN
                    IF TG_OP<>'UPDATE'
                       OR NOT (OLD."Status"=5 AND NEW."Status" IN (2,6)
                           OR OLD."Status"=6 AND NEW."Status"=7
                           OR OLD."Status"=7 AND NEW."Status"=8
                             AND current_user='zeka_auth_purge_fixture')
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
            => throw new NotSupportedException(
                "Irreversible-boundary evidence removal requires a separately reviewed incident procedure.");
    }
}
