using AutoMapper;
using Client.Application.Common.Mappings;

namespace Client.Application.Clients.Model;

public class UpdateClientDto : IMapFrom<Core.Entities.Client>
{
    public string IbisNumber { get; set; }

    public void Mapping(Profile profile)
    {
        profile.CreateMap<UpdateClientDto, Core.Entities.Client>()
            .ReverseMap();
    }
}