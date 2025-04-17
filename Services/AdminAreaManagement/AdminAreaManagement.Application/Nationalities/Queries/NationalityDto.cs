using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Nationalities.Queries
{
    public class NationalityDto : IMapFrom<Nationality>
    {
        public string Name { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Nationality, NationalityDto>();
        }
    }
}
