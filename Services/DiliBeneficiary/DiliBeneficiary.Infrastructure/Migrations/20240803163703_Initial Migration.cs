using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DiliBeneficiary.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Beneficiaries",
                columns: table => new
                {
                    BeneficiaryId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceId = table.Column<int>(type: "integer", nullable: true),
                    ReferenceNumber = table.Column<string>(type: "text", nullable: false),
                    CivilStatus = table.Column<int>(type: "integer", nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: false),
                    LastName = table.Column<string>(type: "text", nullable: false),
                    Gender = table.Column<int>(type: "integer", nullable: false),
                    BirthDate = table.Column<DateTime>(type: "date", nullable: true),
                    StartDateInCpas = table.Column<DateTime>(type: "date", nullable: false),
                    EndDateInCpas = table.Column<DateTime>(type: "date", nullable: true),
                    Nationality = table.Column<string>(type: "text", nullable: false),
                    Niss = table.Column<string>(type: "text", nullable: false),
                    Email_EmailAddress = table.Column<string>(type: "text", nullable: false),
                    Phone_PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    MobilePhone_PhoneNumber = table.Column<string>(type: "text", nullable: false),
                    HasChildren = table.Column<bool>(type: "boolean", nullable: false),
                    NativeLanguage_SpokenLanguage = table.Column<string>(type: "text", nullable: false),
                    ContactLanguage_SpokenLanguage = table.Column<string>(type: "text", nullable: false),
                    Address_Number = table.Column<string>(type: "text", nullable: false),
                    Address_Street = table.Column<string>(type: "text", nullable: false),
                    Address_PostalCode = table.Column<string>(type: "text", nullable: false),
                    Address_City = table.Column<string>(type: "text", nullable: false),
                    SocialWorkerName = table.Column<string>(type: "text", nullable: false),
                    IbisNumber = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Beneficiaries", x => x.BeneficiaryId);
                });

            migrationBuilder.CreateTable(
                name: "ContratPiis",
                columns: table => new
                {
                    ContratPiisId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdSource = table.Column<int>(type: "integer", nullable: false),
                    Libelle = table.Column<string>(type: "text", nullable: false),
                    CommentaireStatut = table.Column<string>(type: "text", nullable: false),
                    AsTraitant = table.Column<string>(type: "text", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DateOfSigning = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratPiis", x => x.ContratPiisId);
                });

            migrationBuilder.CreateTable(
                name: "Languages",
                columns: table => new
                {
                    LanguageId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Languages", x => x.LanguageId);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringActions",
                columns: table => new
                {
                    ActionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringActions", x => x.ActionId);
                });

            migrationBuilder.CreateTable(
                name: "Profession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "School",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Locality = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_School", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Service",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Acronym = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Service", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrainingField",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingField", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrainingType",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingType", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bilans",
                columns: table => new
                {
                    BilanId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsFinalized = table.Column<bool>(type: "boolean", nullable: false),
                    BeneficiaryId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    PersonalSituationFamily = table.Column<string>(type: "text", nullable: false),
                    PersonalSituationHousing = table.Column<string>(type: "text", nullable: false),
                    PersonalSituationHealth = table.Column<string>(type: "text", nullable: false),
                    PersonalSituationFinancialSituation = table.Column<string>(type: "text", nullable: false),
                    PersonalSituationAdministrativeStatus = table.Column<string>(type: "text", nullable: false),
                    LanguageFormationNote = table.Column<string>(type: "text", nullable: false),
                    FormationDifficulty = table.Column<string>(type: "text", nullable: false),
                    FormationOpinion = table.Column<string>(type: "text", nullable: false),
                    FormationFacilitiesAndStrengths = table.Column<string>(type: "text", nullable: false),
                    FormationPersonalImprovments = table.Column<string>(type: "text", nullable: false),
                    FormationConsultantNote = table.Column<string>(type: "text", nullable: false),
                    FormationConsultantLanguageLearningNote = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExperienceProblemEncountered = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExperienceWhatsRewarding = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExperienceKnowledge = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExperiencePointToImprove = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExperienceNote = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationWorkingConditionWhatIWant = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationWorkingConditionWhatIDontWant = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationWorkingConditionWhatMotivatesMe = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationWorkingConditionConsultantNote = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationShortTermA = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationShortTermB = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationMediumTerm = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationLongTerm = table.Column<string>(type: "text", nullable: false),
                    ProfessionalExpectationNlOralLanguageScore = table.Column<int>(type: "integer", nullable: false),
                    ProfessionalExpectationNlWrittentLanguageScore = table.Column<int>(type: "integer", nullable: false),
                    ProfessionalExpectationFrOralLanguageScore = table.Column<int>(type: "integer", nullable: false),
                    ProfessionalExpectationFrWrittenLanguageScore = table.Column<int>(type: "integer", nullable: false),
                    ProfessionalExpectationItKnowledgeEmail = table.Column<bool>(type: "boolean", nullable: false),
                    ProfessionalExpectationItKnowledgeInternet = table.Column<bool>(type: "boolean", nullable: false),
                    ProfessionalExpectationItKnowledgeWord = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bilans", x => x.BilanId);
                    table.ForeignKey(
                        name: "FK_Bilans_Beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalTable: "Beneficiaries",
                        principalColumn: "BeneficiaryId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Referent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FirstName = table.Column<string>(type: "text", nullable: false),
                    LastName = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    ServiceId = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Referent_Service_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Service",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Formation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TrainingFieldId = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Formation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Formation_TrainingField_TrainingFieldId",
                        column: x => x.TrainingFieldId,
                        principalTable: "TrainingField",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProfessionBilans",
                columns: table => new
                {
                    BilanProfessionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BilanId = table.Column<int>(type: "integer", nullable: false),
                    ProfessionId = table.Column<int>(type: "integer", nullable: true),
                    AcquiredKnowledge = table.Column<string>(type: "text", nullable: false),
                    AcquiredBehaviouralKnowledge = table.Column<string>(type: "text", nullable: false),
                    AcquiredKnowHow = table.Column<string>(type: "text", nullable: false),
                    KnowledgeToDevelop = table.Column<string>(type: "text", nullable: false),
                    BehaviouralKnowledgeToDevelop = table.Column<string>(type: "text", nullable: false),
                    KnowHowToDevelop = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfessionBilans", x => x.BilanProfessionId);
                    table.ForeignKey(
                        name: "FK_ProfessionBilans_Bilans_BilanId",
                        column: x => x.BilanId,
                        principalTable: "Bilans",
                        principalColumn: "BilanId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProfessionBilans_Profession_ProfessionId",
                        column: x => x.ProfessionId,
                        principalTable: "Profession",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuarterlyMonitorings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BeneficiaryId = table.Column<int>(type: "integer", nullable: false),
                    ReferentId = table.Column<int>(type: "integer", nullable: false),
                    MonitoringActionId = table.Column<int>(type: "integer", nullable: false),
                    ActionDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActionComment = table.Column<string>(type: "text", nullable: false),
                    Quarter = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuarterlyMonitorings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuarterlyMonitorings_Beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalTable: "Beneficiaries",
                        principalColumn: "BeneficiaryId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuarterlyMonitorings_MonitoringActions_MonitoringActionId",
                        column: x => x.MonitoringActionId,
                        principalTable: "MonitoringActions",
                        principalColumn: "ActionId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuarterlyMonitorings_Referent_ReferentId",
                        column: x => x.ReferentId,
                        principalTable: "Referent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Supports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StartDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReferentId = table.Column<int>(type: "integer", nullable: false),
                    BeneficiaryId = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    ReasonOfClosure = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Supports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Supports_Beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalTable: "Beneficiaries",
                        principalColumn: "BeneficiaryId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Supports_Referent_ReferentId",
                        column: x => x.ReferentId,
                        principalTable: "Referent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SchoolRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FormationId = table.Column<int>(type: "integer", nullable: false),
                    SchoolId = table.Column<int>(type: "integer", nullable: false),
                    TrainingTypeId = table.Column<int>(type: "integer", nullable: false),
                    BeneficiaryId = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EnDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CourseLevel = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Softdelete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchoolRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchoolRegistrations_Beneficiaries_BeneficiaryId",
                        column: x => x.BeneficiaryId,
                        principalTable: "Beneficiaries",
                        principalColumn: "BeneficiaryId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SchoolRegistrations_Formation_FormationId",
                        column: x => x.FormationId,
                        principalTable: "Formation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SchoolRegistrations_School_SchoolId",
                        column: x => x.SchoolId,
                        principalTable: "School",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SchoolRegistrations_TrainingType_TrainingTypeId",
                        column: x => x.TrainingTypeId,
                        principalTable: "TrainingType",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bilans_BeneficiaryId",
                table: "Bilans",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_Formation_TrainingFieldId",
                table: "Formation",
                column: "TrainingFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Languages_Name",
                table: "Languages",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionBilans_BilanId",
                table: "ProfessionBilans",
                column: "BilanId");

            migrationBuilder.CreateIndex(
                name: "IX_ProfessionBilans_ProfessionId",
                table: "ProfessionBilans",
                column: "ProfessionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyMonitorings_BeneficiaryId",
                table: "QuarterlyMonitorings",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyMonitorings_MonitoringActionId",
                table: "QuarterlyMonitorings",
                column: "MonitoringActionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuarterlyMonitorings_ReferentId",
                table: "QuarterlyMonitorings",
                column: "ReferentId");

            migrationBuilder.CreateIndex(
                name: "IX_Referent_ServiceId",
                table: "Referent",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_BeneficiaryId",
                table: "SchoolRegistrations",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_FormationId",
                table: "SchoolRegistrations",
                column: "FormationId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_SchoolId",
                table: "SchoolRegistrations",
                column: "SchoolId");

            migrationBuilder.CreateIndex(
                name: "IX_SchoolRegistrations_TrainingTypeId",
                table: "SchoolRegistrations",
                column: "TrainingTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Supports_BeneficiaryId",
                table: "Supports",
                column: "BeneficiaryId");

            migrationBuilder.CreateIndex(
                name: "IX_Supports_ReferentId",
                table: "Supports",
                column: "ReferentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContratPiis");

            migrationBuilder.DropTable(
                name: "Languages");

            migrationBuilder.DropTable(
                name: "ProfessionBilans");

            migrationBuilder.DropTable(
                name: "QuarterlyMonitorings");

            migrationBuilder.DropTable(
                name: "SchoolRegistrations");

            migrationBuilder.DropTable(
                name: "Supports");

            migrationBuilder.DropTable(
                name: "Bilans");

            migrationBuilder.DropTable(
                name: "Profession");

            migrationBuilder.DropTable(
                name: "MonitoringActions");

            migrationBuilder.DropTable(
                name: "Formation");

            migrationBuilder.DropTable(
                name: "School");

            migrationBuilder.DropTable(
                name: "TrainingType");

            migrationBuilder.DropTable(
                name: "Referent");

            migrationBuilder.DropTable(
                name: "Beneficiaries");

            migrationBuilder.DropTable(
                name: "TrainingField");

            migrationBuilder.DropTable(
                name: "Service");
        }
    }
}
