using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Professions.Queries
{
    public class ProfessionDto : IMapFrom<Profession>
    {
        public int Id { get; set; }
        public string Name { get; set; }

        //public IList<StaffMemberDto> Referents { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Profession, ProfessionDto>();
        }
    }
}
