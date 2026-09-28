using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations;

public partial class OrganisationExportParticipantV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("SET ROLE zeka_client_owner;");
        CreateFence(migrationBuilder);
        CreateFragments(migrationBuilder);
        CreateInbox(migrationBuilder);
        CreateOutbox(migrationBuilder);

        migrationBuilder.CreateIndex("IX_OrganisationExportFences_OperationId_OrganisationId",
            "OrganisationExportFences", new[] { "OperationId", "OrganisationId" }, unique: true);
        migrationBuilder.CreateIndex("IX_OrganisationExportFences_OrganisationId",
            "OrganisationExportFences", "OrganisationId", unique: true, filter: "\"ReleasedAt\" IS NULL");
        migrationBuilder.CreateIndex("IX_OrganisationExportFragments_OperationId_OrganisationId_Cate~",
            "OrganisationExportFragments", new[] { "OperationId", "OrganisationId", "Category" }, unique: true);
        migrationBuilder.CreateIndex("IX_OrganisationExportInbox_OrganisationId_MessageId",
            "OrganisationExportInbox", new[] { "OrganisationId", "MessageId" }, unique: true);
        migrationBuilder.CreateIndex("IX_OrganisationExportOutbox_OrganisationId_MessageId",
            "OrganisationExportOutbox", new[] { "OrganisationId", "MessageId" }, unique: true);

        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION zeka.reject_writes_during_export_fence() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $function$
            DECLARE organisation uuid;
            BEGIN
              IF TG_OP = 'DELETE' THEN organisation := OLD."OrganisationId";
              ELSE organisation := NEW."OrganisationId"; END IF;
              IF organisation IS NULL OR organisation = '00000000-0000-0000-0000-000000000000'::uuid
                OR organisation <> zeka.current_organisation_id() THEN
                RAISE EXCEPTION 'Tenant database context is invalid.';
              END IF;
              PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(pg_catalog.lower(organisation::text), 0));
              IF EXISTS (SELECT 1 FROM public."OrganisationExportFences"
                WHERE "OrganisationId" = organisation AND "ReleasedAt" IS NULL) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'organisation_export_fence_active';
              END IF;
              IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
              RETURN NEW;
            END $function$;
            REVOKE ALL ON FUNCTION zeka.reject_writes_during_export_fence() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION zeka.reject_writes_during_export_fence() TO zeka_client_runtime;

            ALTER TABLE "OrganisationExportFences" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportFences" FORCE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportFragments" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportFragments" FORCE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportInbox" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportInbox" FORCE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportOutbox" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "OrganisationExportOutbox" FORCE ROW LEVEL SECURITY;
            CREATE POLICY rls_organisationexportfences_organisation ON "OrganisationExportFences" FOR ALL TO zeka_client_runtime
              USING ("OrganisationId" = zeka.current_organisation_id()) WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
            CREATE POLICY rls_organisationexportfragments_organisation ON "OrganisationExportFragments" FOR ALL TO zeka_client_runtime
              USING ("OrganisationId" = zeka.current_organisation_id()) WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
            CREATE POLICY rls_organisationexportinbox_organisation ON "OrganisationExportInbox" FOR ALL TO zeka_client_runtime
              USING ("OrganisationId" = zeka.current_organisation_id()) WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
            CREATE POLICY rls_organisationexportoutbox_organisation ON "OrganisationExportOutbox" FOR ALL TO zeka_client_runtime
              USING ("OrganisationId" = zeka.current_organisation_id()) WITH CHECK ("OrganisationId" = zeka.current_organisation_id());

            REVOKE ALL ON TABLE "OrganisationExportFences", "OrganisationExportFragments",
              "OrganisationExportInbox", "OrganisationExportOutbox" FROM PUBLIC;
            GRANT SELECT, INSERT ON TABLE "OrganisationExportFences", "OrganisationExportFragments",
              "OrganisationExportInbox", "OrganisationExportOutbox" TO zeka_client_runtime;
            GRANT UPDATE ("ReleasedAt") ON TABLE "OrganisationExportFences" TO zeka_client_runtime;
            GRANT USAGE, SELECT ON SEQUENCE "OrganisationExportFences_Id_seq", "OrganisationExportFragments_Id_seq",
              "OrganisationExportInbox_Id_seq", "OrganisationExportOutbox_Id_seq" TO zeka_client_runtime;

            CREATE TRIGGER trg_clients_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "Clients"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_socialworkers_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "SocialWorkers"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_socialcases_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "SocialCases"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_assessments_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "Assessments"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_professionalassessments_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "ProfessionalAssessments"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_professionnalexperience_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "ProfessionnalExperience"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_schoolregistrations_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "SchoolRegistrations"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            CREATE TRIGGER trg_monitoringreports_export_fence BEFORE INSERT OR UPDATE OR DELETE ON "MonitoringReports"
              FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_export_fence();
            RESET ROLE;
            """);
    }

    private static void CreateFence(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "OrganisationExportFences",
        columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            OperationId = table.Column<Guid>(type: "uuid", nullable: false), OperationRevision = table.Column<long>(type: "bigint", nullable: false),
            ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            FenceRevision = table.Column<long>(type: "bigint", nullable: false), EnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false), ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CreatedBy = table.Column<string>(type: "text", nullable: false), Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false), LastModifiedBy = table.Column<string>(type: "text", nullable: false), LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true), Softdelete = table.Column<bool>(type: "boolean", nullable: false), OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_OrganisationExportFences", x => x.Id); table.UniqueConstraint("AK_OrganisationExportFences_Id_OrganisationId", x => new { x.Id, x.OrganisationId }); });

    private static void CreateFragments(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "OrganisationExportFragments",
        columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            OperationId = table.Column<Guid>(type: "uuid", nullable: false), OperationRevision = table.Column<long>(type: "bigint", nullable: false), ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), FenceToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), FenceEvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false), SnapshotAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false), Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false), RecordCount = table.Column<long>(type: "bigint", nullable: false), SoftDeletedRecordCount = table.Column<long>(type: "bigint", nullable: false), SchemaVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true), ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true), ArtifactReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true), ReasonCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true), CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            CreatedBy = table.Column<string>(type: "text", nullable: false), Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false), LastModifiedBy = table.Column<string>(type: "text", nullable: false), LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true), Softdelete = table.Column<bool>(type: "boolean", nullable: false), OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_OrganisationExportFragments", x => x.Id); table.UniqueConstraint("AK_OrganisationExportFragments_Id_OrganisationId", x => new { x.Id, x.OrganisationId }); });

    private static void CreateInbox(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "OrganisationExportInbox",
        columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            MessageId = table.Column<Guid>(type: "uuid", nullable: false), OperationId = table.Column<Guid>(type: "uuid", nullable: false), OperationRevision = table.Column<long>(type: "bigint", nullable: false), RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false), ResponseType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), ResponseJson = table.Column<string>(type: "jsonb", nullable: false), ResultMessageId = table.Column<Guid>(type: "uuid", nullable: false), CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            CreatedBy = table.Column<string>(type: "text", nullable: false), Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false), LastModifiedBy = table.Column<string>(type: "text", nullable: false), LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true), Softdelete = table.Column<bool>(type: "boolean", nullable: false), OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_OrganisationExportInbox", x => x.Id); table.UniqueConstraint("AK_OrganisationExportInbox_Id_OrganisationId", x => new { x.Id, x.OrganisationId }); });

    private static void CreateOutbox(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "OrganisationExportOutbox",
        columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            MessageId = table.Column<Guid>(type: "uuid", nullable: false), OperationId = table.Column<Guid>(type: "uuid", nullable: false), MessageType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false), PayloadJson = table.Column<string>(type: "jsonb", nullable: false), PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false), OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false), PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CreatedBy = table.Column<string>(type: "text", nullable: false), Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false), LastModifiedBy = table.Column<string>(type: "text", nullable: false), LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true), Softdelete = table.Column<bool>(type: "boolean", nullable: false), OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_OrganisationExportOutbox", x => x.Id); table.UniqueConstraint("AK_OrganisationExportOutbox_Id_OrganisationId", x => new { x.Id, x.OrganisationId }); });

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Export fence rollback requires an explicitly approved incident procedure.");
}
