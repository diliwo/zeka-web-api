using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Core.Entities;
using AutoMapper;

namespace AdminAreaManagement.Application.DocumentPartners.Queries
{
   public class DocumentPartnerDto : IMapFrom<DocumentPartner>
    {
        public int DocumentPartnerId { get; set; }
        public int PartnerId { get; set; }
        public string Name { get; set; }
        public string ContentType { get; set; }
        public byte[] ContentFile { get; set; }
        public string Description { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<DocumentPartner, DocumentPartnerDto>()
                .ForMember(b => b.DocumentPartnerId,
                    opt => opt.MapFrom(e => e.Id));
        }
    }
}
