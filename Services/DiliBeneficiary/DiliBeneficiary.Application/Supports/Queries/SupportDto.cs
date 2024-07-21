using AutoMapper;
using DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaries;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Supports.Queries
{
    public class SupportDto : IMapFrom<Support>
    {
        public int SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; } = DateTime.Now;
        public int ReferentId { get; set; }
        public int BeneficiaryId { get; set; }
        public string ReferentInfo { get; set; }
        public BeneficiaryLookUpDto Beneficiary { get; set; }
        public bool IsActif { get; set; }
        public string Note { get; set; }
        public string? ReasonOfClosure { get; set; }
        public bool HasNote { get; set; }
        public bool IsLastSupport { get; set; }
        public void Mapping(Profile profile)
        {
            profile.CreateMap<Support, SupportDto>()
                .ForMember(b => b.SupportId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.ReferentInfo,
                    opt =>
                        opt.MapFrom(e =>
                            e.ReferentId != null
                                ? e.Referent.FullName + ' ' + '(' + e.Referent.Service.Acronym + ')'
                                : string.Empty))
                .ForMember(s => s.HasNote, 
                    opt => 
                        opt.MapFrom(e =>  e.Note.Length > 0 ? true : false))
                .ForMember(l => l.IsLastSupport,
                    opt =>
                        opt.MapFrom(s => (s.Beneficiary.Supports.Where(s => s.Softdelete != true).OrderBy(c => c.Id).Last().Id == s.Id) ? true : false))
                .ForMember(r => r.ReasonOfClosure,
                    opt => opt.MapFrom(e => e.ReasonOfClosure));
        }
    }
}
