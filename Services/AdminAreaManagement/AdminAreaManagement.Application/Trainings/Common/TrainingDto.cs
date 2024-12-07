using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.Formations.Common
{
    public class TrainingDto: IMapFrom<Training>
    {
        public int TrainingId { get; set; }
        public string Name { get; set; }
        public string TrainingFieldName { get; set; }
        public int TrainingFieldId { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Training, TrainingDto>()
                .ForMember(f => f.TrainingId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember( s => s.TrainingFieldName,
                    opt => opt.MapFrom(e => e.TrainingField.Name))
                .ForMember(s => s.TrainingFieldId,
                    opt => opt.MapFrom(e => e.TrainingField.Id));
        }
    }
}