using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ClientManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClientActionWritePrerequisite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET ROLE zeka_client_owner;");
            migrationBuilder.CreateTable(
                name: "Interactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    SocialCaseId = table.Column<int>(type: "integer", nullable: false),
                    AuthorMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interactions", x => x.Id);
                    table.UniqueConstraint("AK_Interactions_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                    table.CheckConstraint("CK_Interactions_Revision", "\"Revision\" = 1");
                    table.ForeignKey(
                        name: "FK_Interactions_Clients_ClientId_OrganisationId",
                        columns: x => new { x.ClientId, x.OrganisationId },
                        principalTable: "Clients",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Interactions_SocialCases_SocialCaseId_OrganisationId",
                        columns: x => new { x.SocialCaseId, x.OrganisationId },
                        principalTable: "SocialCases",
                        principalColumns: new[] { "SchoolRegistrationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FollowUpActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    SocialCaseId = table.Column<int>(type: "integer", nullable: false),
                    InteractionId = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DueLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AssigneeMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUpActions", x => x.Id);
                    table.UniqueConstraint("AK_FollowUpActions_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                    table.CheckConstraint("CK_FollowUpActions_State", "\"State\" IN ('Open', 'Completed')");
                    table.ForeignKey(
                        name: "FK_FollowUpActions_Clients_ClientId_OrganisationId",
                        columns: x => new { x.ClientId, x.OrganisationId },
                        principalTable: "Clients",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FollowUpActions_Interactions_InteractionId_OrganisationId",
                        columns: x => new { x.InteractionId, x.OrganisationId },
                        principalTable: "Interactions",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FollowUpActions_SocialCases_SocialCaseId_OrganisationId",
                        columns: x => new { x.SocialCaseId, x.OrganisationId },
                        principalTable: "SocialCases",
                        principalColumns: new[] { "SchoolRegistrationId", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActionChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FollowUpActionId = table.Column<int>(type: "integer", nullable: false),
                    ActorMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PriorVersion = table.Column<int>(type: "integer", nullable: false),
                    NewVersion = table.Column<int>(type: "integer", nullable: false),
                    PriorState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NewState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PriorAssigneeMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewAssigneeMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriorDueLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    NewDueLocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionChanges", x => x.Id);
                    table.UniqueConstraint("AK_ActionChanges_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                    table.ForeignKey(
                        name: "FK_ActionChanges_FollowUpActions_FollowUpActionId_Organisation~",
                        columns: x => new { x.FollowUpActionId, x.OrganisationId },
                        principalTable: "FollowUpActions",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientId = table.Column<int>(type: "integer", nullable: false),
                    CanonicalMetadata = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    InteractionId = table.Column<int>(type: "integer", nullable: true),
                    FollowUpActionId = table.Column<int>(type: "integer", nullable: true),
                    ActionChangeId = table.Column<int>(type: "integer", nullable: true),
                    ResultVersion = table.Column<int>(type: "integer", nullable: false),
                    RetainUntilUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false),
                    OrganisationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyReceipts", x => x.Id);
                    table.UniqueConstraint("AK_IdempotencyReceipts_Id_OrganisationId", x => new { x.Id, x.OrganisationId });
                    table.ForeignKey(
                        name: "FK_IdempotencyReceipts_ActionChanges_ActionChangeId_Organisati~",
                        columns: x => new { x.ActionChangeId, x.OrganisationId },
                        principalTable: "ActionChanges",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdempotencyReceipts_Clients_ClientId_OrganisationId",
                        columns: x => new { x.ClientId, x.OrganisationId },
                        principalTable: "Clients",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdempotencyReceipts_FollowUpActions_FollowUpActionId_Organi~",
                        columns: x => new { x.FollowUpActionId, x.OrganisationId },
                        principalTable: "FollowUpActions",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdempotencyReceipts_Interactions_InteractionId_Organisation~",
                        columns: x => new { x.InteractionId, x.OrganisationId },
                        principalTable: "Interactions",
                        principalColumns: new[] { "Id", "OrganisationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionChanges_FollowUpActionId_OrganisationId",
                table: "ActionChanges",
                columns: new[] { "FollowUpActionId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionChanges_OrganisationId_FollowUpActionId_NewVersion",
                table: "ActionChanges",
                columns: new[] { "OrganisationId", "FollowUpActionId", "NewVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpActions_ClientId_OrganisationId",
                table: "FollowUpActions",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpActions_InteractionId_OrganisationId",
                table: "FollowUpActions",
                columns: new[] { "InteractionId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpActions_OrganisationId_ClientId_State_DueLocalDate",
                table: "FollowUpActions",
                columns: new[] { "OrganisationId", "ClientId", "State", "DueLocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpActions_SocialCaseId_OrganisationId",
                table: "FollowUpActions",
                columns: new[] { "SocialCaseId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyReceipts_ActionChangeId_OrganisationId",
                table: "IdempotencyReceipts",
                columns: new[] { "ActionChangeId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyReceipts_ClientId_OrganisationId",
                table: "IdempotencyReceipts",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyReceipts_FollowUpActionId_OrganisationId",
                table: "IdempotencyReceipts",
                columns: new[] { "FollowUpActionId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyReceipts_InteractionId_OrganisationId",
                table: "IdempotencyReceipts",
                columns: new[] { "InteractionId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyReceipts_OrganisationId_ActorMembershipId_Operat~",
                table: "IdempotencyReceipts",
                columns: new[] { "OrganisationId", "ActorMembershipId", "Operation", "ClientKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_ClientId_OrganisationId",
                table: "Interactions",
                columns: new[] { "ClientId", "OrganisationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_OrganisationId_ClientId_OccurredAtUtc",
                table: "Interactions",
                columns: new[] { "OrganisationId", "ClientId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_SocialCaseId_OrganisationId",
                table: "Interactions",
                columns: new[] { "SocialCaseId", "OrganisationId" });

            migrationBuilder.Sql("""
                ALTER TABLE "Interactions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Interactions" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "FollowUpActions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "FollowUpActions" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "ActionChanges" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "ActionChanges" FORCE ROW LEVEL SECURITY;
                ALTER TABLE "IdempotencyReceipts" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "IdempotencyReceipts" FORCE ROW LEVEL SECURITY;

                CREATE POLICY rls_interactions_organisation ON "Interactions" FOR ALL
                  TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_followupactions_organisation ON "FollowUpActions" FOR ALL
                  TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_actionchanges_organisation ON "ActionChanges" FOR ALL
                  TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                CREATE POLICY rls_idempotencyreceipts_organisation ON "IdempotencyReceipts" FOR ALL
                  TO zeka_client_runtime, zeka_client_owner
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());

                REVOKE ALL ON TABLE "Interactions", "FollowUpActions", "ActionChanges",
                  "IdempotencyReceipts" FROM PUBLIC;
                GRANT SELECT, INSERT ON TABLE "Interactions", "ActionChanges",
                  "IdempotencyReceipts" TO zeka_client_runtime;
                GRANT SELECT, INSERT, UPDATE ON TABLE "FollowUpActions" TO zeka_client_runtime;
                GRANT USAGE, SELECT ON SEQUENCE "Interactions_Id_seq", "FollowUpActions_Id_seq",
                  "ActionChanges_Id_seq", "IdempotencyReceipts_Id_seq" TO zeka_client_runtime;

                CREATE TRIGGER trg_interactions_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "Interactions"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_followupactions_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "FollowUpActions"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_actionchanges_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "ActionChanges"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                CREATE TRIGGER trg_idempotencyreceipts_closure_fence BEFORE INSERT OR UPDATE OR DELETE ON "IdempotencyReceipts"
                  FOR EACH ROW EXECUTE FUNCTION zeka.reject_writes_during_closure_fence();
                RESET ROLE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Action-write history rollback requires an approved preservation procedure.");
        }
    }
}
