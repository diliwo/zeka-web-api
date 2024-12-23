using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Teams.Queries
{
    public class TeamDto : IMapFrom<Team>
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Acronym { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Team, TeamDto>();
        }
    }
}
