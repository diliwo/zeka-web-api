using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Partners.Queries.Common
{
    public class PartnerSelectionListDto : IMapFrom<Partner>
    {
        public int PartnerId { get; set; }
        public String Name { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Partner, PartnerSelectionListDto>()
                .ForMember(b => b.PartnerId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(p => p.Name,
                    opt => opt.MapFrom(r => r.Name));
        }
    }
}
