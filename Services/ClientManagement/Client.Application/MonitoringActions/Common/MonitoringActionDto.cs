﻿using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.MonitoringActions.Common
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
