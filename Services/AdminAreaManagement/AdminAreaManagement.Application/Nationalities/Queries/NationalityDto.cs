using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Nationalities.Queries
{
    public class NationalityDto : IMapFrom<City>
    {
        public string Name { get; set; }
        public string Country { get; set; }
        public string CountryDemonym { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<City, NationalityDto>();
        }
    }
}
