using Client.Core.Entities;

namespace Client.Core.Interfaces
{

    public interface IClientRepository
    {
        void Persist(Entities.Client client);
        Entities.Client Get(int id, bool trackChanges = false);

        Task<Entities.Client> GetSourceIdAsync(int id, bool trackChanges = false);
        Task<Entities.Client> GetAsync(int id,bool trackChanges = false);
        Entities.Client GetClientByNiss(string niss, bool trackChanges = false);
        Task<Entities.Client> GetClientByNissAsync(string niss, bool trackChanges = false);
        Task<List<string>> GetClientNissesAsync(bool trackChanges = false);
        IQueryable<Entities.Client> GetClients();
        Task<IEnumerable<Entities.Client>> GetClientsAsync(bool trackChanges);
        IQueryable<Entities.Client> GetClientsBySearchText(string text);
    }
}
