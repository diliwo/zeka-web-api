using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.TrainingTypes.Common
{
    public class TrainingTypeDto : IMapFrom<TrainingType>
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<TrainingType, TrainingTypeDto>()
                .ForMember(f => f.Id,
                    opt => opt.MapFrom(e => e.Id));
        }
    }
}