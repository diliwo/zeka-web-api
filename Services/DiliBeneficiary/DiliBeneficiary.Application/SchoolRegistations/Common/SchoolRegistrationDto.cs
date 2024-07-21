using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Application.SchoolRegistations.Common
{
    public class SchoolRegistrationDto : IMapFrom<School>
    {
        public int SchoolRegistrationId { get; set; }
        public int FormationId { get; set; }
        public string FormationName { get; set; }
        public int SchoolId { get; set; }
        public string SchoolName { get; set; }
        public int TrainingTypeId { get; set; }
        public string TrainingTypeName { get; set; }
        public int BeneficiaryId { get; set; }
        public string BenficiaryName { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<SchoolRegistration, SchoolRegistrationDto>()
                .ForMember(s => s.SchoolRegistrationId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(s => s.FormationName,
                    opt => opt.MapFrom(e => e.Formation.Name))
                .ForMember(s => s.BenficiaryName,
                    opt => opt.MapFrom(e => e.Beneficiary.FullName))
                .ForMember(s => s.SchoolName,
                    opt => opt.MapFrom(e => e.School.Name))
                .ForMember(s => s.TrainingTypeName,
                    opt => opt.MapFrom(e => e.TrainingType.Name));
        }
    }
}