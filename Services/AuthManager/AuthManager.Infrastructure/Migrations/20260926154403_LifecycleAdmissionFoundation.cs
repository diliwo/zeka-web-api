using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleAdmissionFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET ROLE zeka_auth_owner;
                DO $provisioning$
                BEGIN
                  IF NOT EXISTS (
                    SELECT FROM pg_catalog.pg_namespace
                    WHERE nspname='zeka' AND pg_catalog.pg_get_userbyid(nspowner)='zeka_auth_owner'
                  ) THEN
                    RAISE EXCEPTION 'AuthManagement administrative schema provisioning is required.';
                  END IF;
                END $provisioning$;
                """);
            migrationBuilder.CreateTable(
                name: "LifecycleParticipantRegistryRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleParticipantRegistryRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleParticipantRegistryActivation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleParticipantRegistryActivation", x => x.Id);
                    table.CheckConstraint("CK_LifecycleActivation_Singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_LifecycleParticipantRegistryActivation_LifecycleParticipant~",
                        column: x => x.RevisionId,
                        principalTable: "LifecycleParticipantRegistryRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleParticipantRegistryBindings",
                columns: table => new
                {
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Family = table.Column<int>(type: "integer", nullable: false),
                    CapabilityKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OwnershipScope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleParticipantRegistryBindings", x => new { x.RevisionId, x.Family, x.CapabilityKey, x.OwnershipScope });
                    table.ForeignKey(
                        name: "FK_LifecycleParticipantRegistryBindings_LifecycleParticipantRe~",
                        column: x => x.RevisionId,
                        principalTable: "LifecycleParticipantRegistryRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganisationLifecycleOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Family = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestingSubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistryRevision = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationLifecycleOperations", x => x.Id);
                    table.UniqueConstraint("AK_OrganisationLifecycleOperations_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                    table.ForeignKey(
                        name: "FK_OrganisationLifecycleOperations_LifecycleParticipantRegistr~",
                        column: x => x.RegistryRevision,
                        principalTable: "LifecycleParticipantRegistryRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganisationLifecycleParticipants",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Family = table.Column<int>(type: "integer", nullable: false),
                    CapabilityKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OwnershipScope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    Mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganisationLifecycleParticipants", x => new { x.OperationId, x.Family, x.CapabilityKey, x.OwnershipScope });
                    table.ForeignKey(
                        name: "FK_OrganisationLifecycleParticipants_OrganisationLifecycleOper~",
                        columns: x => new { x.OperationId, x.OrganisationId },
                        principalTable: "OrganisationLifecycleOperations",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleParticipantRegistryActivation_RevisionId",
                table: "LifecycleParticipantRegistryActivation",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationLifecycleOperations_OrganisationId_Family",
                table: "OrganisationLifecycleOperations",
                columns: new[] { "OrganisationId", "Family" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationLifecycleOperations_OrganisationId_IdempotencyId",
                table: "OrganisationLifecycleOperations",
                columns: new[] { "OrganisationId", "IdempotencyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationLifecycleOperations_RegistryRevision",
                table: "OrganisationLifecycleOperations",
                column: "RegistryRevision");

            migrationBuilder.CreateIndex(
                name: "IX_OrganisationLifecycleParticipants_OperationId_OrganisationId",
                table: "OrganisationLifecycleParticipants",
                columns: new[] { "OperationId", "OrganisationId" });

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION zeka.current_organisation_id() RETURNS uuid
                LANGUAGE plpgsql SECURITY INVOKER
                SET search_path = pg_catalog
                AS $function$
                DECLARE raw text; parsed uuid;
                BEGIN
                  raw := current_setting('zeka.organisation_id', true);
                  IF raw IS NULL OR raw = '' THEN RAISE EXCEPTION 'Tenant database context is invalid.'; END IF;
                  BEGIN parsed := raw::uuid;
                  EXCEPTION WHEN invalid_text_representation THEN RAISE EXCEPTION 'Tenant database context is invalid.';
                  END;
                  IF raw <> lower(parsed::text) OR parsed = '00000000-0000-0000-0000-000000000000'::uuid THEN
                    RAISE EXCEPTION 'Tenant database context is invalid.';
                  END IF;
                  RETURN parsed;
                END $function$;
                REVOKE ALL ON FUNCTION zeka.current_organisation_id() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_auth_runtime;

                ALTER TABLE public."OrganisationLifecycleOperations" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."OrganisationLifecycleOperations" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_organisationlifecycleoperations_organisation
                  ON public."OrganisationLifecycleOperations" TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                ALTER TABLE public."OrganisationLifecycleParticipants" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."OrganisationLifecycleParticipants" FORCE ROW LEVEL SECURITY;
                CREATE POLICY rls_organisationlifecycleparticipants_organisation
                  ON public."OrganisationLifecycleParticipants" TO zeka_auth_runtime
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());

                REVOKE ALL ON TABLE public."LifecycleParticipantRegistryRevisions",
                  public."LifecycleParticipantRegistryBindings", public."LifecycleParticipantRegistryActivation",
                  public."OrganisationLifecycleOperations", public."OrganisationLifecycleParticipants"
                  FROM PUBLIC, zeka_auth_runtime;
                GRANT SELECT ON TABLE public."LifecycleParticipantRegistryRevisions",
                  public."LifecycleParticipantRegistryBindings", public."LifecycleParticipantRegistryActivation"
                  TO zeka_auth_runtime;
                GRANT SELECT, INSERT ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
                GRANT UPDATE ("State", "Revision") ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
                GRANT SELECT, INSERT ON TABLE public."OrganisationLifecycleParticipants" TO zeka_auth_runtime;
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Lifecycle ledger removal requires a separately reviewed migration.");
        }
    }
}
