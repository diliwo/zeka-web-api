using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;

namespace ClientManagement.Application.Assessments.Common
{
    public class AssessmentDto : IMapFrom<Assessment>
    {
        public int BilanId { get; set; }
        public bool IsFinalized { get; set; } = false;
        public int ClientId { get; set; }
        public string UserName { get; set; }
        public DateTime CreationDate { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageTrainingNote { get; set; }
        public string TrainingDifficulty { get; set; }
        public string TrainingOpinion { get; set; }
        public string TrainingFacilitiesAndStrengths { get; set; }
        public string TrainingPersonalImprovments { get; set; }
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
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlWrittentLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrWrittenLanguageScore { get; set; }
        public bool ProfessionalExpectationItKnowledgeEmail { get; set; }
        public bool ProfessionalExpectationItKnowledgeInternet { get; set; }
        public bool ProfessionalExpectationItKnowledgeWord { get; set; }
        public IEnumerable<ProfessionalAssessmentDto> BilanProfessions { get; set; } = new List<ProfessionalAssessmentDto>();

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Assessment, AssessmentDto>()
                .ForMember(f => f.BilanId,
                    opt => opt.MapFrom(b => b.Id))
                .ForMember(b => b.CreationDate,
                    opt => opt.MapFrom(b => b.Created));
        }
    }
}
