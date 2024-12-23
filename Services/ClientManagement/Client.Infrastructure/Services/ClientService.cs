using ClientManagement.Core.Interfaces;

namespace ClientManagement.Infrastructure.Services;

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