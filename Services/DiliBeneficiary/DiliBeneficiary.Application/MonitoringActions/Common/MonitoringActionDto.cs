using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.MonitoringActions.Common
{
    public class MonitoringActionDto : IMapFrom<MonitoringAction>
    {
        public int ActionId { get; set; }
        public string ActionLabel { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<MonitoringAction, MonitoringActionDto>()
                .ForMember(d => d.ActionId,
                    opt => opt.MapFrom(s => s.Id))
                .ForMember(d => d.ActionLabel,
                    opt => opt.MapFrom(s => s.Action));
        }
    }
}
