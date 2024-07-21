using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaryDetail
{
    public class ContratPiisDto : IMapFrom<ContratPiis>
    {
        public string Libelle { get; set; }
        public string AsTraitant { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<ContratPiis, ContratPiisDto>();
        }
    }
}
