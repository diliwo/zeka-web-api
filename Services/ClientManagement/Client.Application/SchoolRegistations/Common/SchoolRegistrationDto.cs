using AutoMapper;
using Client.Application.Common.Mappings;
using Client.Core.Entities;
using Client.Core.Enums;

namespace Client.Application.SchoolRegistations.Common
{
    public class SchoolRegistrationDto : IMapFrom<School>
    {
        public int SchoolRegistrationId { get; set; }
        public int TrainingId { get; set; }
        public string TrainingName { get; set; }
        public int SchoolId { get; set; }
        public string SchoolName { get; set; }
        public int TrainingTypeId { get; set; }
        public string TrainingTypeName { get; set; }
        public int ClientId { get; set; }
        public string BenficiaryName { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<SchoolEnrollment, SchoolRegistrationDto>()
                .ForMember(s => s.SchoolRegistrationId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(s => s.TrainingName,
                    opt => opt.MapFrom(e => e.Training.Name))
                .ForMember(s => s.BenficiaryName,
                    opt => opt.MapFrom(e => e.Client.FullName))
                .ForMember(s => s.SchoolName,
                    opt => opt.MapFrom(e => e.School.Name))
                .ForMember(s => s.TrainingTypeName,
                    opt => opt.MapFrom(e => e.TrainingType.Name));
        }
    }
}