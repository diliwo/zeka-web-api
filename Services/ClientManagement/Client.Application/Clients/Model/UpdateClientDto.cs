using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Clients.Model;

public class UpdateClientDto : IMapFrom<Client>
{
    public string IbisNumber { get; set; }

    public void Mapping(Profile profile)
    {
        profile.CreateMap<UpdateClientDto, Client>()
            .ReverseMap();
    }
}