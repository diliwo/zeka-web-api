using System.Collections;
using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.StaffMembers.Queries
{
    public class StaffMemberDto : IMapFrom<StaffMember>
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int TeamId { get; set; }
        public string TeamName { get; set; }
        public string UserName { get; set; }
        public string TeamAcronymName { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<StaffMember, StaffMemberDto>()
                .ForMember(b => b.Id,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.TeamAcronymName, 
                    opt => opt.MapFrom(e => e.Team.Acronym))
                .ForMember(r => r.TeamName, opt => opt.MapFrom(s => s.Team.Acronym)); ;
        }
    }
}
