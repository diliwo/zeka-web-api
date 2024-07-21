using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaries
{
    public class BeneficiaryLookUpDto : IMapFrom<Beneficiary>
    {
        public int BeneficiaryId { get; set; }
        public string ReferenceNumber { get; set; }
        public string Name { get; set; }
        public string Niss { get; set; }
        public string Gender { get; set; }


        public void Mapping(Profile profile)
        {
            profile.CreateMap<Beneficiary, BeneficiaryLookUpDto>()
                .ForMember(b => b.BeneficiaryId, opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.Name, opt => opt.MapFrom(e => e.LastName + " " + e.FirstName))
                .ForMember(b => b.Gender,
                    opt => opt.MapFrom(e => e.Gender == Core.Enums.Gender.Male ? 'H' : 'F'));

        }
    }
}
