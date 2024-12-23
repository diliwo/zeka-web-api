using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Schools.Common
{
    public class SchoolDto : IMapFrom<School>
    {
        public int SchoolId { get; set; }
        public string Name { get; set; }
        public string Locality { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<School, SchoolDto>()
                .ForMember(s => s.SchoolId,
                    opt => opt.MapFrom(e => e.Id));
        }
    }
}