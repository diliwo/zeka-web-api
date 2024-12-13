using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.ValueObjects;
using AutoMapper;

namespace AdminAreaManagement.Application.Partners.Queries.Common
{
    public class PartnerDto : IMapFrom<Partner>
    {
        public int PartnerId { get; set; }
        public int PartnerNumber { get; set; }
        public String Name { get; set; }
        public Address Address { get; set; }
        public List<ContactPerson> ContactPersons { get; set; }
        public int JobCoachId { get; set; }
        public string JobCoachName { get; set; }
        public CategoryOfPartner CategoryOfPartner { get; set; }
        public string? CategoryOfPartnerName { get; set; }
        public StatusOfPartner StatusOfPartner { get; set; }
        public string StatusOfPartnerName { get; set; }
        public DateTime DateOfAgreementSignature { get; set; }
        public DateTime DateOfConclusion { get; set; }
        public Boolean IsEconomieSociale { get; set; }
        public String? Note { get; set; }
        public Boolean Softdelete { get; set; }
        public bool HasDocuments { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Partner, PartnerDto>()
                .ForMember(b => b.PartnerId,
                    opt => opt.MapFrom(e => e.Id))
                .ForMember(p => p.ContactPersons,
                    opt => opt.MapFrom(b => b.Contacts))
                .ForMember(p => p.CategoryOfPartnerName,
                    opt => opt.MapFrom(b => Enum.GetName((CategoryOfPartner) b.CategoryOfPartner)))
                .ForMember(p => p.HasDocuments,
                    opt => opt.MapFrom(p => p.Documents != null && p.Documents.Any()));
        }
    }
}
