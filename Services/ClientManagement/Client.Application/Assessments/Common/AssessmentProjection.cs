using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Assessments.Common;

internal static class AssessmentProjection
{
    internal static IQueryable<AssessmentDto> ProjectAssessmentDtos(this IQueryable<Assessment> source) =>
        source.Select(assessment => new AssessmentDto
        {
            BilanId = assessment.Id,
            IsFinalized = assessment.IsFinalized,
            ClientId = assessment.ClientId ?? 0,
            UserName = assessment.UserName,
            CreationDate = assessment.Created,
            PersonalSituationFamily = assessment.PersonalSituationFamily,
            PersonalSituationHousing = assessment.PersonalSituationHousing,
            PersonalSituationHealth = assessment.PersonalSituationHealth,
            PersonalSituationFinancialSituation = assessment.PersonalSituationFinancialSituation,
            PersonalSituationAdministrativeStatus = assessment.PersonalSituationAdministrativeStatus,
            LanguageTrainingNote = assessment.LanguageTrainingNote,
            TrainingDifficulty = assessment.TrainingDifficulty,
            TrainingOpinion = assessment.TrainingOpinion,
            TrainingFacilitiesAndStrengths = assessment.TrainingFacilitiesAndStrengths,
            TrainingPersonalImprovments = assessment.TrainingPersonalImprovments,
            TrainingConsultantNote = assessment.TrainingConsultantNote,
            TrainingConsultantLanguageLearningNote = assessment.TrainingConsultantLanguageLearningNote,
            ProfessionalExperienceProblemEncountered = assessment.ProfessionalExperienceProblemEncountered,
            ProfessionalExperienceWhatsRewarding = assessment.ProfessionalExperienceWhatsRewarding,
            ProfessionalExperienceKnowledge = assessment.ProfessionalExperienceKnowledge,
            ProfessionalExperiencePointToImprove = assessment.ProfessionalExperiencePointToImprove,
            ProfessionalExperienceNote = assessment.ProfessionalExperienceNote,
            ProfessionalExpectationWorkingConditionWhatIWant = assessment.ProfessionalExpectationWorkingConditionWhatIWant,
            ProfessionalExpectationWorkingConditionWhatIDontWant = assessment.ProfessionalExpectationWorkingConditionWhatIDontWant,
            ProfessionalExpectationWorkingConditionWhatMotivatesMe = assessment.ProfessionalExpectationWorkingConditionWhatMotivatesMe,
            ProfessionalExpectationWorkingConditionConsultantNote = assessment.ProfessionalExpectationWorkingConditionConsultantNote,
            ProfessionalExpectationShortTermA = assessment.ProfessionalExpectationShortTermA,
            ProfessionalExpectationShortTermB = assessment.ProfessionalExpectationShortTermB,
            ProfessionalExpectationMediumTerm = assessment.ProfessionalExpectationMediumTerm,
            ProfessionalExpectationLongTerm = assessment.ProfessionalExpectationLongTerm,
            ProfessionalExpectationNlOralLanguageScore = assessment.ProfessionalExpectationNlOralLanguageScore,
            ProfessionalExpectationNlWrittentLanguageScore = assessment.ProfessionalExpectationNlWrittentLanguageScore,
            ProfessionalExpectationFrOralLanguageScore = assessment.ProfessionalExpectationFrOralLanguageScore,
            ProfessionalExpectationFrWrittenLanguageScore = assessment.ProfessionalExpectationFrWrittenLanguageScore,
            ProfessionalExpectationItKnowledgeEmail = assessment.ProfessionalExpectationItKnowledgeEmail,
            ProfessionalExpectationItKnowledgeInternet = assessment.ProfessionalExpectationItKnowledgeInternet,
            ProfessionalExpectationItKnowledgeWord = assessment.ProfessionalExpectationItKnowledgeWord,
            BilanProfessions = assessment.BilanProfessions.Select(profession => new ProfessionalAssessmentDto
            {
                // The existing wire projection leaves BilanId and BenficiaryId at their defaults.
                BilanProfessionId = profession.Id,
                ProfessionTitle = profession.Profession!.Name,
                ProfessionId = profession.ProfessionId,
                AcquiredKnowledge = profession.AcquiredKnowledge,
                AcquiredBehaviouralKnowledge = profession.AcquiredBehaviouralKnowledge,
                AcquiredKnowHow = profession.AcquiredKnowHow,
                KnowledgeToDevelop = profession.KnowledgeToDevelop,
                BehaviouralKnowledgeToDevelop = profession.BehaviouralKnowledgeToDevelop,
                KnowHowToDevelop = profession.KnowHowToDevelop
            }).ToList()
        });
}
