using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableDocumentFileOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_adminarea_owner;");

            migrationBuilder.AddColumn<Guid>(
                name: "CreateOperationId",
                table: "DocumentPartners",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "CreateRequestHash",
                table: "DocumentPartners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "DeleteOperationId",
                table: "DocumentPartners",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FileDeleteAttempts",
                table: "DocumentPartners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FileDeleteFailureCode",
                table: "DocumentPartners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FileDeleteState",
                table: "DocumentPartners",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FileWriteAttempts",
                table: "DocumentPartners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FileWriteFailureCode",
                table: "DocumentPartners",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FileWriteState",
                table: "DocumentPartners",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte[]>(
                name: "PendingFileContent",
                table: "DocumentPartners",
                type: "bytea",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "DocumentPartners"
                SET "CreateOperationId" = pg_catalog.md5("OrganisationId"::text || ':' || "Id"::text)::uuid,
                    "CreateRequestHash" = pg_catalog.md5('legacy:' || "OrganisationId"::text || ':' || "Id"::text)
                        || pg_catalog.md5('document:' || "OrganisationId"::text || ':' || "Id"::text),
                    "FileWriteState" = 2,
                    "FileWriteFailureCode" = 'legacy_storage_state_unknown'
                WHERE "CreateOperationId" = '00000000-0000-0000-0000-000000000000'::uuid;
                ALTER TABLE "DocumentPartners"
                  ADD CONSTRAINT "CK_DocumentPartners_CreateOperationId_NonEmpty"
                    CHECK ("CreateOperationId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                  ADD CONSTRAINT "CK_DocumentPartners_CreateRequestHash_Length"
                    CHECK (char_length("CreateRequestHash") = 64),
                  ADD CONSTRAINT "CK_DocumentPartners_DeleteOperationId_NonEmpty"
                    CHECK ("DeleteOperationId" IS NULL OR "DeleteOperationId" <> '00000000-0000-0000-0000-000000000000'::uuid);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPartners_OrganisationId_CreateOperationId",
                table: "DocumentPartners",
                columns: new[] { "OrganisationId", "CreateOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPartners_OrganisationId_DeleteOperationId",
                table: "DocumentPartners",
                columns: new[] { "OrganisationId", "DeleteOperationId" },
                unique: true);

            migrationBuilder.Sql("RESET ROLE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Durable document-operation rollback requires an explicitly approved incident procedure.");
    }
}
