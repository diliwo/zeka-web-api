using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleExportCoordinatorV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_auth_owner;");
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "OrganisationLifecycleOperations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FenceEvidenceHash",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageReference",
                table: "OrganisationLifecycleOperations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageSha256",
                table: "OrganisationLifecycleOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SnapshotAt",
                table: "OrganisationLifecycleOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LifecycleCoordinatorLeases",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleCoordinatorLeases", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_LifecycleCoordinatorLeases_OrganisationLifecycleOperations_~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleExportFenceReceipts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleExportFenceReceipts", x => new { x.OperationId, x.ParticipantId });
                    table.ForeignKey(
                        name: "FK_LifecycleExportFenceReceipts_OrganisationLifecycleOperation~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleExportFragments",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FragmentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CategoriesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleExportFragments", x => new { x.OperationId, x.ParticipantId });
                    table.ForeignKey(
                        name: "FK_LifecycleExportFragments_OrganisationLifecycleOperations_Op~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleExportPackages",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackageReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleExportPackages", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_LifecycleExportPackages_OrganisationLifecycleOperations_Ope~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleInboxReceipts",
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
                    table.PrimaryKey("PK_LifecycleInboxReceipts", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_LifecycleInboxReceipts_OrganisationLifecycleOperations_Oper~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            foreach (var table in new[]
                     {
                         "LifecycleCoordinatorLeases", "LifecycleExportFenceReceipts",
                         "LifecycleExportFragments", "LifecycleExportPackages"
                     })
                migrationBuilder.CreateIndex(
                    name: $"IX_{table}_OperationId_OrganisationId",
                    table: table,
                    columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleInboxReceipts_OperationId_MessageType",
                table: "LifecycleInboxReceipts",
                columns: new[] { "OperationId", "MessageType" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleInboxReceipts_OperationId_OrganisationId",
                table: "LifecycleInboxReceipts",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.Sql("""
                DO $life01_rls$
                DECLARE object_name text;
                BEGIN
                  FOREACH object_name IN ARRAY ARRAY[
                    'LifecycleCoordinatorLeases','LifecycleExportFenceReceipts','LifecycleExportFragments',
                    'LifecycleExportPackages','LifecycleInboxReceipts'
                  ] LOOP
                    EXECUTE pg_catalog.format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', object_name);
                    EXECUTE pg_catalog.format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', object_name);
                    EXECUTE pg_catalog.format(
                      'CREATE POLICY rls_%s_organisation ON public.%I TO zeka_auth_runtime USING ("OrganisationId" = zeka.current_organisation_id()) WITH CHECK ("OrganisationId" = zeka.current_organisation_id())',
                      pg_catalog.lower(object_name), object_name);
                    EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM PUBLIC, zeka_auth_runtime', object_name);
                    EXECUTE pg_catalog.format('GRANT SELECT, INSERT ON TABLE public.%I TO zeka_auth_runtime', object_name);
                  END LOOP;
                  GRANT UPDATE ("LeaseId", "ExpiresAt", "Version")
                    ON TABLE public."LifecycleCoordinatorLeases" TO zeka_auth_runtime;
                  GRANT UPDATE ("State") ON TABLE public."OrganisationLifecycleParticipants" TO zeka_auth_runtime;
                  GRANT UPDATE ("State", "Revision", "SnapshotAt", "FenceEvidenceHash", "PackageSha256",
                    "PackageReference", "FailureCode", "CompletedAt", "IsActive")
                    ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
                END $life01_rls$;

                CREATE OR REPLACE FUNCTION zeka.reject_auth_membership_write_during_export_fence()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY INVOKER
                SET search_path = pg_catalog, public, zeka
                AS $function$
                DECLARE scoped_organisation uuid;
                BEGIN
                  scoped_organisation := CASE WHEN TG_OP='DELETE' THEN OLD."OrganisationId" ELSE NEW."OrganisationId" END;
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
                REVOKE ALL ON FUNCTION zeka.reject_auth_membership_write_during_export_fence() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.reject_auth_membership_write_during_export_fence() TO zeka_auth_runtime;
                CREATE TRIGGER trg_auth_membership_export_fence
                  BEFORE INSERT OR UPDATE OR DELETE ON public."OrganisationMemberships"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_auth_membership_write_during_export_fence();
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_auth_membership_export_fence ON public."OrganisationMemberships";
                DROP FUNCTION IF EXISTS zeka.reject_auth_membership_write_during_export_fence();
                """);
            foreach (var table in new[]
                     {
                         "LifecycleCoordinatorLeases", "LifecycleExportFenceReceipts",
                         "LifecycleExportFragments", "LifecycleExportPackages", "LifecycleInboxReceipts"
                     })
                migrationBuilder.DropTable(name: table);

            foreach (var column in new[]
                     {
                         "CompletedAt", "FailureCode", "FenceEvidenceHash",
                         "PackageReference", "PackageSha256", "SnapshotAt"
                     })
                migrationBuilder.DropColumn(name: column, table: "OrganisationLifecycleOperations");
        }
    }
}
