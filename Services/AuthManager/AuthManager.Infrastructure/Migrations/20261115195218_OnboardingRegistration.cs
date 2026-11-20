using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthManager.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailVerificationChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AddressBinding = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StampBinding = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TransitionedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerificationChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerificationChallenges_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CredentialHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StampBinding = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingGrants_EmailVerificationChallenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "EmailVerificationChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingOperationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingOperationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnboardingOperationReceipts_EmailVerificationChallenges_Cha~",
                        column: x => x.ChallengeId,
                        principalTable: "EmailVerificationChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VerificationMailOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                    EnqueuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObsoleteAtAcceptance = table.Column<bool>(type: "boolean", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationMailOutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationMailOutboxMessages_EmailVerificationChallenges_~",
                        column: x => x.ChallengeId,
                        principalTable: "EmailVerificationChallenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationChallenges_AddressBinding_CreatedAtUtc",
                table: "EmailVerificationChallenges",
                columns: new[] { "AddressBinding", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationChallenges_UserId",
                table: "EmailVerificationChallenges",
                column: "UserId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerificationChallenges_UserId_Generation",
                table: "EmailVerificationChallenges",
                columns: new[] { "UserId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingGrants_ChallengeId",
                table: "OnboardingGrants",
                column: "ChallengeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingGrants_CredentialHash",
                table: "OnboardingGrants",
                column: "CredentialHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingOperationReceipts_ChallengeId",
                table: "OnboardingOperationReceipts",
                column: "ChallengeId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationMailOutboxMessages_ChallengeId_Generation",
                table: "VerificationMailOutboxMessages",
                columns: new[] { "ChallengeId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationMailOutboxMessages_OperationId",
                table: "VerificationMailOutboxMessages",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationMailOutboxMessages_Status_NextAttemptAtUtc",
                table: "VerificationMailOutboxMessages",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnboardingGrants");

            migrationBuilder.DropTable(
                name: "OnboardingOperationReceipts");

            migrationBuilder.DropTable(
                name: "VerificationMailOutboxMessages");

            migrationBuilder.DropTable(
                name: "EmailVerificationChallenges");
        }
    }
}
