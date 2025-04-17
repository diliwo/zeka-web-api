using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Cities.Queries
{
    public class CityDto : IMapFrom<City>
    {
        public string Name { get; set; }
        public string Country { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<City, CityDto>();
        }
    }
}
