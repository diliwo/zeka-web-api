using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class ClientRepository : RepositoryBase<ClientManagement.Core.Entities.Client>,IClientRepository
    {

        public ClientRepository(ApplicationDbContext context) : base(context){}

        public void Persist(ClientManagement.Core.Entities.Client client)
        {
            if (client.Id == default(int))
            {
                // Insert
                Create(client);
            }
            else
            {
                //Update
                Update(client);
            }

        }

        public ClientManagement.Core.Entities.Client Get(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefault();
        }

        public Task<ClientManagement.Core.Entities.Client> GetAsync(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefaultAsync();
        }

        public ClientManagement.Core.Entities.Client GetClientBySsN(string niss, bool trackChanges = false)
        {
            return FindByCondition(Client => Client.Ssn.Equals(niss), trackChanges)
                .SingleOrDefault();
        }

        public Task<ClientManagement.Core.Entities.Client> GetClientByNissAsync(string niss, bool trackChanges = false)
        {
            return FindByCondition(Client => Client.Ssn.Equals(niss), trackChanges)
                .SingleOrDefaultAsync();
        }

        public IQueryable<ClientManagement.Core.Entities.Client> GetClients()
        {
            return FindAll(false);
        }

        public async Task<IEnumerable<ClientManagement.Core.Entities.Client>> GetClientsAsync(bool trackChanges) => await FindAll(trackChanges).ToListAsync();

        public async Task<List<string>> GetClientNissesAsync(bool trackChanges) => await FindAll(trackChanges).Select(n => n.Ssn).ToListAsync();

        public IQueryable<Client> GetClientsBySearchText(string text)
        {
            var clients = FindAll( false).AsExpandable();

            if (!string.IsNullOrEmpty(text))
            {
                var predicate = PredicateBuilder.New<Client>();

                predicate = predicate.Or(p => p.FirstName.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.LastName.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.Ssn.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.ReferenceNumber.ToLower().Contains(text.ToLower().Trim()));

                clients = clients.Where(predicate);
            }

            return clients;
        }
    }
}
