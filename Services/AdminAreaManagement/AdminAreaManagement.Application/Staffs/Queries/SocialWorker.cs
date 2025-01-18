using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;
using System;

namespace AdminAreaManagement.Application.Staffs.Queries
{
    public class SocialWorker : IMapFrom<StaffMember>
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string UserName { get; set; }
        public string TeamName { get; set; }
        public string TeamAcronym { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<StaffMember, SocialWorker>()
                .ForMember(b => b.Id,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.TeamAcronym, 
                    opt => opt.MapFrom(e => e.Team.Acronym))
                .ForMember(r => r.TeamName, opt => opt.MapFrom(s => s.Team.Acronym)); ;
        }
    }
}
