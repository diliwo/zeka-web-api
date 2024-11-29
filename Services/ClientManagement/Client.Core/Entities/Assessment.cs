using ClientManagement.Core.Common;
using ClientManagement.Core.Enums;

namespace ClientManagement.Core.Entities
{
    public class Assessment : Entity
    {
        public Boolean IsFinalized { get; set; } = false;
        public int ClientId { get; set; }
        public Client Client { get; set; }
        public string UserName { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageTrainingNote { get; set; }
        public string TrainingDifficulty { get; set; }
        public string TrainingOpinion { get; set; }
        public string TrainingFacilitiesAndStrengths { get; set; }
        public string TrainingPersonalImprovments { get; set;  }
        public string TrainingConsultantNote { get; set; }
        public string TrainingConsultantLanguageLearningNote { get; set; }
        public string ProfessionalExperienceProblemEncountered { get; set; }
        public string ProfessionalExperienceWhatsRewarding { get; set; }
        public string ProfessionalExperienceKnowledge { get; set; }
        public string ProfessionalExperiencePointToImprove { get; set; }
        public string ProfessionalExperienceNote { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatIWant { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatIDontWant { get; set; }
        public string ProfessionalExpectationWorkingConditionWhatMotivatesMe { get; set; }
        public string ProfessionalExpectationWorkingConditionConsultantNote { get; set; }
        public string ProfessionalExpectationShortTermA { get; set; }
        public string ProfessionalExpectationShortTermB { get; set; }
        public string ProfessionalExpectationMediumTerm { get; set; }
        public string ProfessionalExpectationLongTerm { get; set; }
        public PersonalExpectationLanguageknowledgeScore  ProfessionalExpectationNlOralLanguageScore { get; set; } = PersonalExpectationLanguageknowledgeScore.NotAtAll;
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlWrittentLanguageScore { get; set; } = PersonalExpectationLanguageknowledgeScore.NotAtAll;
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrOralLanguageScore { get; set; } = PersonalExpectationLanguageknowledgeScore.NotAtAll;
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrWrittenLanguageScore { get; set; } = PersonalExpectationLanguageknowledgeScore.NotAtAll;
        public bool ProfessionalExpectationItKnowledgeEmail { get; set; } = false;
        public bool ProfessionalExpectationItKnowledgeInternet { get; set; } = false;
        public bool ProfessionalExpectationItKnowledgeWord { get; set; } = false; 
        public IList<ProfessionalAssessment> BilanProfessions { get; set; } = new List<ProfessionalAssessment>();

        public Assessment(){}

        public Assessment(
            Boolean isFinalized,
            Client client,
            string userName,
            string personalSituationFamily,
            string personalSituationHousing,
            string personalSituationHealth,
            string personalSituationFinancialSituation,
            string personalSituationAdministrativeStatus,
            string languageTrainingNote,
            string TrainingDifficulty,
            string TrainingOpinion,
            string TrainingFacilitiesAndStrengths,
            string TrainingPersonalImprovments,
            string TrainingConsultantNote,
            string TrainingConsultantLanguageLearningNote,
            string professionalExperienceProblemEncountered,
            string professionalExperienceWhatsRewarding,
            string professionalExperienceKnowledge,
            string professionalExperiencePointToImprove,
            string professionalExperienceNote,
            string professionalExpectationWorkingConditionWhatIWant,
            string professionalExpectationWorkingConditionWhatIDontWant,
            string professionalExpectationWorkingConditionWhatMotivatesMe,
            string professionalExpectationWorkingConditionConsultantNote,
            string professionalExpectationShortTermA,
            string professionalExpectationShortTermB,
            string professionalExpectationMediumTerm,
            string professionalExpectationLongTerm,
            PersonalExpectationLanguageknowledgeScore professionalExpectationNlOralLanguageScore,
            PersonalExpectationLanguageknowledgeScore professionalExpectationNlWrittentLanguageScore,
            PersonalExpectationLanguageknowledgeScore professionalExpectationFrOralLanguageScore,
            PersonalExpectationLanguageknowledgeScore professionalExpectationFrWrittenLanguageScore,
            bool professionalExpectationItKnowledgeEmail,
            bool professionalExpectationItKnowledgeInternet,
            bool professionalExpectationItKnowledgeWord
        )
        {

            //if (TrainingDifficulty.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(TrainingDifficulty));
            //}
            //if (TrainingOpinion.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(TrainingOpinion));
            //}
            //if (TrainingFacilitiesAndStrengths.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(TrainingFacilitiesAndStrengths));
            //}
            //if (TrainingPersonalImprovments.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(TrainingPersonalImprovments));
            //}
            //if (TrainingDifficulty.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(TrainingDifficulty));
            //}

            IsFinalized = isFinalized;
            Client = client;
            UserName = userName;
            PersonalSituationFamily = personalSituationFamily;
            PersonalSituationHousing = personalSituationHousing;
            PersonalSituationHealth = personalSituationHealth;
            PersonalSituationFinancialSituation = personalSituationFinancialSituation;
            PersonalSituationAdministrativeStatus = personalSituationAdministrativeStatus;
            LanguageTrainingNote = languageTrainingNote;
            TrainingDifficulty = TrainingDifficulty;
            TrainingOpinion = TrainingOpinion;
            TrainingFacilitiesAndStrengths = TrainingFacilitiesAndStrengths;
            TrainingPersonalImprovments = TrainingPersonalImprovments;
            TrainingConsultantNote = TrainingConsultantNote;
            TrainingConsultantLanguageLearningNote = TrainingConsultantLanguageLearningNote;
            ProfessionalExperienceProblemEncountered = professionalExperienceProblemEncountered;
            ProfessionalExperienceWhatsRewarding = professionalExperienceWhatsRewarding;
            ProfessionalExperienceKnowledge = professionalExperienceKnowledge;
            ProfessionalExperiencePointToImprove = professionalExperiencePointToImprove;
            ProfessionalExperienceNote = professionalExperienceNote;
            ProfessionalExpectationWorkingConditionWhatIWant  = professionalExpectationWorkingConditionWhatIWant;
            ProfessionalExpectationWorkingConditionWhatIDontWant = professionalExpectationWorkingConditionWhatIDontWant;
            ProfessionalExpectationWorkingConditionWhatMotivatesMe =
                professionalExpectationWorkingConditionWhatMotivatesMe;
            ProfessionalExpectationWorkingConditionConsultantNote =
                professionalExpectationWorkingConditionConsultantNote;
            ProfessionalExpectationShortTermA = professionalExpectationShortTermA;
            ProfessionalExpectationShortTermB = professionalExpectationShortTermB;
            ProfessionalExpectationMediumTerm = professionalExpectationMediumTerm;
            ProfessionalExpectationLongTerm = professionalExpectationLongTerm;
            ProfessionalExpectationNlOralLanguageScore = professionalExpectationNlOralLanguageScore;
            ProfessionalExpectationNlWrittentLanguageScore = professionalExpectationNlWrittentLanguageScore;
            ProfessionalExpectationFrOralLanguageScore = professionalExpectationFrOralLanguageScore;
            ProfessionalExpectationFrWrittenLanguageScore = professionalExpectationFrWrittenLanguageScore;
            ProfessionalExpectationItKnowledgeEmail = professionalExpectationItKnowledgeEmail;
            ProfessionalExpectationItKnowledgeInternet = professionalExpectationItKnowledgeInternet;
            ProfessionalExpectationItKnowledgeWord = professionalExpectationItKnowledgeWord;

            //ProfessionalExpectationNlOralLanguageScore = PersonalExpectationLanguageknowledgeScore.NotAtAll;
            //ProfessionalExpectationFrOralLanguageScore = PersonalExpectationLanguageknowledgeScore.NotAtAll;
            //ProfessionalExpectationNlWrittentLanguageScore = PersonalExpectationLanguageknowledgeScore.NotAtAll;
            //ProfessionalExpectationFrWrittenLanguageScore = PersonalExpectationLanguageknowledgeScore.NotAtAll;
            //ProfessionalExpectationItKnowledgeEmail = false;
            //ProfessionalExpectationItKnowledgeInternet = false;
            //ProfessionalExpectationItKnowledgeWord = false;
        }

        public IEnumerable<ProfessionalAssessment> Professions
        {
            get => BilanProfessions;
        }

        public void CreateBilanProfession(Profession profession,
            string AcquiredKnowledge,
            string AcquiredBehaviouralKnowledge,
            string AcquiredKnowHow,
            string KnowledgeToDevelop, 
            string BehaviouralKnowledgeToDevelop, 
            string KnowHowToDevelop)
        {
            ProfessionalAssessment newProfessionalAssessment;

            if (profession != null)
            {
                newProfessionalAssessment = new ProfessionalAssessment(
                    this,
                    profession,
                    AcquiredKnowledge,
                    AcquiredBehaviouralKnowledge,
                    AcquiredKnowHow,
                    KnowledgeToDevelop,
                    BehaviouralKnowledgeToDevelop,
                    KnowHowToDevelop
                );

                if (newProfessionalAssessment != null)
                {
                    BilanProfessions.Add(newProfessionalAssessment);
                }
            }
        }
    }
}