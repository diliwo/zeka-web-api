using Client.Core.Common.Dto;
using Client.Core.Entities;
using Client.Core.Interfaces;

namespace Client.Infrastructure.Services;

public class ClientService : IClientService
{
    public readonly IRepositoryManager _repository;

    public ClientService(
        IRepositoryManager repository
        )
    {
        _repository = repository;
    }

    public async Task<int> Update(List<string> nisses)
    {
        return 0;
    }

    public async Task<int> UpSert(string niss)
    {
        return 0;
    }
}