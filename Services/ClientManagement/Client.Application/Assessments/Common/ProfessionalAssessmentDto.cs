using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Assessments.Common
{
    public class ProfessionalAssessmentDto : IMapFrom<ProfessionalAssessmentDto>
    {
        public int? BilanProfessionId { get; set; }
        public int BilanId { get; set; }
        public string ProfessionTitle { get; set; }
        public int? ProfessionId { get; set; }
        public int? BenficiaryId { get; set; }
        public string AcquiredKnowledge { get; set; }
        public string AcquiredBehaviouralKnowledge { get; set; }
        public string AcquiredKnowHow { get; set; }
        public string KnowledgeToDevelop { get; set; }
        public string BehaviouralKnowledgeToDevelop { get; set; }
        public string KnowHowToDevelop { get; set; }
        public void Mapping(Profile profile)
        {
            profile.CreateMap<ProfessionalAssessment, ProfessionalAssessmentDto>()
                .ForMember(b => b.BilanProfessionId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.ProfessionTitle,
                    opt => opt.MapFrom(e => e.Profession.Name));
        }
    }
}
