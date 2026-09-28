using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Life01AdminAreaExportParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_adminarea_owner;");
            migrationBuilder.CreateTable(
                name: "AdminAreaExportFences",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationRevision = table.Column<long>(type: "bigint", nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    FenceRevision = table.Column<long>(type: "bigint", nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaExportFences", x => new { x.OperationId, x.OrganisationId });
                });

            migrationBuilder.CreateTable(
                name: "AdminAreaExportFragments",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FenceToken = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    FragmentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaExportFragments", x => new { x.OperationId, x.OrganisationId, x.ParticipantId });
                });

            migrationBuilder.CreateTable(
                name: "AdminAreaExportInbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandType = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaExportInbox", x => new { x.MessageId, x.OrganisationId });
                });

            migrationBuilder.CreateTable(
                name: "AdminAreaExportOutbox",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAreaExportOutbox", x => new { x.MessageId, x.OrganisationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreaExportFences_OrganisationId",
                table: "AdminAreaExportFences",
                column: "OrganisationId",
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAreaExportFences_OrganisationId_FenceToken",
                table: "AdminAreaExportFences",
                columns: new[] { "OrganisationId", "FenceToken" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zeka.reject_adminarea_write_during_export() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER
                SET search_path = pg_catalog
                AS $function$
                DECLARE organisation uuid;
                BEGIN
                  organisation := CASE WHEN TG_OP = 'DELETE' THEN OLD."OrganisationId" ELSE NEW."OrganisationId" END;
                  PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(organisation::text, 4601));
                  IF EXISTS (
                    SELECT 1 FROM public."AdminAreaExportFences" f
                    WHERE f."OrganisationId" = organisation AND f."ReleasedAt" IS NULL
                  ) THEN
                    RAISE EXCEPTION USING ERRCODE = '55000',
                      MESSAGE = 'Organisation writes are blocked by an active export fence.';
                  END IF;
                  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                REVOKE ALL ON FUNCTION zeka.reject_adminarea_write_during_export() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.reject_adminarea_write_during_export() TO zeka_adminarea_runtime;
                """);

            foreach (var table in new[]
                     { "Teams", "StaffMembers", "Partners", "ContactPersons", "Emails", "DocumentPartners" })
            {
                migrationBuilder.Sql($$"""
                    DROP TRIGGER IF EXISTS trg_export_fence_{{table.ToLowerInvariant()}} ON "{{table}}";
                    CREATE TRIGGER trg_export_fence_{{table.ToLowerInvariant()}}
                      BEFORE INSERT OR UPDATE OR DELETE ON "{{table}}"
                      FOR EACH ROW EXECUTE FUNCTION zeka.reject_adminarea_write_during_export();
                    """);
            }

            foreach (var table in new[]
                     { "AdminAreaExportFences", "AdminAreaExportFragments", "AdminAreaExportInbox", "AdminAreaExportOutbox" })
            {
                var policy = $"rls_{table.ToLowerInvariant()}_organisation";
                migrationBuilder.Sql($$"""
                    ALTER TABLE "{{table}}" OWNER TO zeka_adminarea_owner;
                    ALTER TABLE "{{table}}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{{table}}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY {{policy}} ON "{{table}}" FOR ALL TO zeka_adminarea_runtime
                      USING ("OrganisationId" = zeka.current_organisation_id())
                      WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                    REVOKE ALL ON TABLE "{{table}}" FROM PUBLIC;
                    GRANT SELECT, INSERT ON TABLE "{{table}}" TO zeka_adminarea_runtime;
                    """);
            }
            migrationBuilder.Sql("GRANT UPDATE (\"ReleasedAt\") ON TABLE \"AdminAreaExportFences\" TO zeka_adminarea_runtime;");
            migrationBuilder.Sql("RESET ROLE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Export fence security rollback requires an explicitly approved incident procedure.");
        }
    }
}
