using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Beneficiaries.Model;

public class UpdateBeneficaryDto : IMapFrom<Beneficiary>
{
    public string IbisNumber { get; set; }

    public void Mapping(Profile profile)
    {
        profile.CreateMap<UpdateBeneficaryDto, Beneficiary>()
            .ReverseMap();
    }
}