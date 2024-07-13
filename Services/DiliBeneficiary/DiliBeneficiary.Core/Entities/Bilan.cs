using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Core.Entities
{
    public class Bilan : Entity
    {
        public Boolean IsFinalized { get; set; } = false;
        public int BeneficiaryId { get; set; }
        public Beneficiary Beneficiary { get; set; }
        public string UserName { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageFormationNote { get; set; }
        public string FormationDifficulty { get; set; }
        public string FormationOpinion { get; set; }
        public string FormationFacilitiesAndStrengths { get; set; }
        public string FormationPersonalImprovments { get; set;  }
        public string FormationConsultantNote { get; set; }
        public string FormationConsultantLanguageLearningNote { get; set; }
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
        public IList<BilanProfession> BilanProfessions { get; set; } = new List<BilanProfession>();

        public Bilan(){}

        public Bilan(
            Boolean isFinalized,
            Beneficiary beneficiary,
            string userName,
            string personalSituationFamily,
            string personalSituationHousing,
            string personalSituationHealth,
            string personalSituationFinancialSituation,
            string personalSituationAdministrativeStatus,
            string languageFormationNote,
            string formationDifficulty,
            string formationOpinion,
            string formationFacilitiesAndStrengths,
            string formationPersonalImprovments,
            string formationConsultantNote,
            string formationConsultantLanguageLearningNote,
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

            //if (formationDifficulty.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(formationDifficulty));
            //}
            //if (formationOpinion.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(formationOpinion));
            //}
            //if (formationFacilitiesAndStrengths.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(formationFacilitiesAndStrengths));
            //}
            //if (formationPersonalImprovments.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(formationPersonalImprovments));
            //}
            //if (formationDifficulty.Length > 255)
            //{
            //    throw new StringSizeLimitException(nameof(formationDifficulty));
            //}

            IsFinalized = isFinalized;
            Beneficiary = beneficiary;
            UserName = userName;
            PersonalSituationFamily = personalSituationFamily;
            PersonalSituationHousing = personalSituationHousing;
            PersonalSituationHealth = personalSituationHealth;
            PersonalSituationFinancialSituation = personalSituationFinancialSituation;
            PersonalSituationAdministrativeStatus = personalSituationAdministrativeStatus;
            LanguageFormationNote = languageFormationNote;
            FormationDifficulty = formationDifficulty;
            FormationOpinion = formationOpinion;
            FormationFacilitiesAndStrengths = formationFacilitiesAndStrengths;
            FormationPersonalImprovments = formationPersonalImprovments;
            FormationConsultantNote = formationConsultantNote;
            FormationConsultantLanguageLearningNote = formationConsultantLanguageLearningNote;
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

        public IEnumerable<BilanProfession> Professions
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
            BilanProfession newBilanProfession;

            if (profession != null)
            {
                newBilanProfession = new BilanProfession(
                    this,
                    profession,
                    AcquiredKnowledge,
                    AcquiredBehaviouralKnowledge,
                    AcquiredKnowHow,
                    KnowledgeToDevelop,
                    BehaviouralKnowledgeToDevelop,
                    KnowHowToDevelop
                );

                if (newBilanProfession != null)
                {
                    BilanProfessions.Add(newBilanProfession);
                }
            }
        }
    }
}