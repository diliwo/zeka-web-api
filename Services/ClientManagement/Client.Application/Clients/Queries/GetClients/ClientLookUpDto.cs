using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Clients.Queries.GetClients
{
    public class ClientLookUpDto : IMapFrom<Client>
    {
        public int ClientId { get; set; }
        public string ReferenceNumber { get; set; }
        public string Name { get; set; }
        public string Niss { get; set; }
        public string Gender { get; set; }


        public void Mapping(Profile profile)
        {
            profile.CreateMap<Client, ClientLookUpDto>()
                .ForMember(b => b.ClientId, opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.Name, opt => opt.MapFrom(e => e.LastName + " " + e.FirstName))
                .ForMember(b => b.Gender,
                    opt => opt.MapFrom(e => e.Gender == Core.Enums.Gender.Male ? 'H' : 'F'));

        }
    }
}
