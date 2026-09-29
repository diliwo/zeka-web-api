using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Life05aVerificationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.AddColumn<string>(
                name: "VerificationEvidenceHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LifecycleVerificationCommands",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    CommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleVerificationCommands", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_LifecycleVerificationCommands_LifecyclePurgePlans_PlanId_Op~",
                        columns: x => new { x.PlanId, x.OperationId, x.OrganisationId },
                        principalTable: "LifecyclePurgePlans",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleVerificationEvidence",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CommandMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerifierVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EligibleResidualCount = table.Column<int>(type: "integer", nullable: false),
                    RetainedPresentCount = table.Column<int>(type: "integer", nullable: false),
                    RetainedExpectedCount = table.Column<int>(type: "integer", nullable: false),
                    FileResidualCount = table.Column<int>(type: "integer", nullable: true),
                    PostconditionSatisfied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleVerificationEvidence", x => new { x.OperationId, x.Category });
                    table.ForeignKey(
                        name: "FK_LifecycleVerificationEvidence_LifecyclePurgePlans_PlanId_Op~",
                        columns: x => new { x.PlanId, x.OperationId, x.OrganisationId },
                        principalTable: "LifecyclePurgePlans",
                        principalColumns: new[] { "Id", "OperationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleVerificationCommands_OperationId_Category_IssuedAt",
                table: "LifecycleVerificationCommands",
                columns: new[] { "OperationId", "Category", "IssuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleVerificationCommands_PlanId_OperationId_Organisati~",
                table: "LifecycleVerificationCommands",
                columns: new[] { "PlanId", "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleVerificationEvidence_PlanId_OperationId_Organisati~",
                table: "LifecycleVerificationEvidence",
                columns: new[] { "PlanId", "OperationId", "OrganisationId" });
            migrationBuilder.Sql("""
                ALTER TABLE public."LifecycleVerificationCommands" OWNER TO zeka_auth_owner;
                ALTER TABLE public."LifecycleVerificationEvidence" OWNER TO zeka_auth_owner;
                ALTER TABLE public."LifecycleVerificationCommands" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecycleVerificationCommands" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecycleVerificationEvidence" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LifecycleVerificationEvidence" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_lifecycleverificationcommands_organisation
                  ON public."LifecycleVerificationCommands" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_lifecycleverificationevidence_organisation
                  ON public."LifecycleVerificationEvidence" FOR ALL TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                REVOKE ALL ON TABLE public."LifecycleVerificationCommands",
                  public."LifecycleVerificationEvidence" FROM PUBLIC, zeka_auth_runtime;
                CREATE OR REPLACE FUNCTION zeka.reject_auth_organisation_write_during_closure_fence()
                RETURNS trigger LANGUAGE plpgsql SECURITY INVOKER
                SET search_path = pg_catalog, public, zeka
                AS $function$
                DECLARE scoped_organisation uuid;
                BEGIN
                  scoped_organisation := CASE WHEN TG_OP='DELETE' THEN OLD."Id" ELSE NEW."Id" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended(scoped_organisation::text, 0));
                  IF TG_OP='UPDATE' AND
                    ((OLD."Status"=7 AND NEW."Status"=8)
                      OR (OLD."Status"=8 AND NEW."Status"=9))
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
                             AND current_user='zeka_auth_purge_fixture'
                           OR OLD."Status"=8 AND NEW."Status"=9
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
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.DropTable(
                name: "LifecycleVerificationCommands");

            migrationBuilder.DropTable(
                name: "LifecycleVerificationEvidence");

            migrationBuilder.DropColumn(
                name: "VerificationEvidenceHash",
                table: "OrganisationLifecycleOperations");
            migrationBuilder.Sql("""
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
                """);
            migrationBuilder.Sql("RESET ROLE;");
        }
    }
}
