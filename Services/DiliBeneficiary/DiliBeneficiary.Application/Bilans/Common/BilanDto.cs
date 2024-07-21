using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Application.Bilans.Common
{
    public class BilanDto : IMapFrom<Bilan>
    {
        public int BilanId { get; set; }
        public bool IsFinalized { get; set; } = false;
        public int BeneficiaryId { get; set; }
        public string UserName { get; set; }
        public DateTime CreationDate { get; set; }
        public string PersonalSituationFamily { get; set; }
        public string PersonalSituationHousing { get; set; }
        public string PersonalSituationHealth { get; set; }
        public string PersonalSituationFinancialSituation { get; set; }
        public string PersonalSituationAdministrativeStatus { get; set; }
        public string LanguageFormationNote { get; set; }
        public string FormationDifficulty { get; set; }
        public string FormationOpinion { get; set; }
        public string FormationFacilitiesAndStrengths { get; set; }
        public string FormationPersonalImprovments { get; set; }
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
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationNlWrittentLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrOralLanguageScore { get; set; }
        public PersonalExpectationLanguageknowledgeScore ProfessionalExpectationFrWrittenLanguageScore { get; set; }
        public bool ProfessionalExpectationItKnowledgeEmail { get; set; }
        public bool ProfessionalExpectationItKnowledgeInternet { get; set; }
        public bool ProfessionalExpectationItKnowledgeWord { get; set; }
        public IEnumerable<BilanProfessionDto> BilanProfessions { get; set; } = new List<BilanProfessionDto>();

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Bilan, BilanDto>()
                .ForMember(f => f.BilanId,
                    opt => opt.MapFrom(b => b.Id))
                .ForMember(b => b.CreationDate,
                    opt => opt.MapFrom(b => b.Created));
        }
    }
}
