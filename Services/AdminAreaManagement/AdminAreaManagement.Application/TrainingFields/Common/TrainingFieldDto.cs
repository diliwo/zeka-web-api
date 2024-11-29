using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.TrainingFields.Common
{
    public class TrainingFieldDto : IMapFrom<TrainingField>
    {
        public int TrainingFieldId { get; set; }
        public string Name { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<TrainingField, TrainingFieldDto>()
                .ForMember(f => f.TrainingFieldId,
                    opt => opt.MapFrom(e => e.Id));
        }
    }
}