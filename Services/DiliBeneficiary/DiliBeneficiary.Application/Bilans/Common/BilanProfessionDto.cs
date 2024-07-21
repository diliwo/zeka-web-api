using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Bilans.Common
{
    public class BilanProfessionDto : IMapFrom<BilanProfessionDto>
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
            profile.CreateMap<BilanProfession, BilanProfessionDto>()
                .ForMember(b => b.BilanProfessionId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.ProfessionTitle,
                    opt => opt.MapFrom(e => e.Profession.Name));
        }
    }
}
