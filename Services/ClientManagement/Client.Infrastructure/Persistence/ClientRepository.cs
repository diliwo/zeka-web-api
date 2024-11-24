using Client.Core.Entities;
using Client.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace Client.Infrastructure.Persistence
{
    public class ClientRepository : RepositoryBase<Core.Entities.Client>,IClientRepository
    {

        public ClientRepository(ApplicationDbContext context) : base(context){}

        public void Persist(Core.Entities.Client client)
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

        public Core.Entities.Client Get(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefault();
        }

        public Task<Core.Entities.Client> GetSourceIdAsync(int sourceId, bool trackChanges = false)
        {
            return FindByCondition(b => b.SourceId.Equals(sourceId), trackChanges).FirstOrDefaultAsync();
        }

        public Task<Core.Entities.Client> GetAsync(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefaultAsync();
        }

        public Core.Entities.Client GetClientByNiss(string niss, bool trackChanges = false)
        {
            return FindByCondition(Client => Client.Niss.Equals(niss), trackChanges)
                .SingleOrDefault();
        }

        public Task<Core.Entities.Client> GetClientByNissAsync(string niss, bool trackChanges = false)
        {
            return FindByCondition(Client => Client.Niss.Equals(niss), trackChanges)
                .SingleOrDefaultAsync();
        }

        public IQueryable<Core.Entities.Client> GetClients()
        {
            return FindAll(false);
        }

        public async Task<IEnumerable<Core.Entities.Client>> GetClientsAsync(bool trackChanges) => await FindAll(trackChanges).ToListAsync();

        public async Task<List<string>> GetClientNissesAsync(bool trackChanges) => await FindAll(trackChanges).Select(n => n.Niss).ToListAsync();

        public IQueryable<Core.Entities.Client> GetClientsBySearchText(string text)
        {
            var benficiaries = FindByCondition(p => p.Softdelete != true, false).AsExpandable();

            if (!string.IsNullOrEmpty(text))
            {
                var predicate = PredicateBuilder.New<Core.Entities.Client>();

                predicate = predicate.Or(p => p.FirstName.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.LastName.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.Niss.ToLower().Contains(text.ToLower().Trim()));
                predicate = predicate.Or(p => p.ReferenceNumber.ToLower().Contains(text.ToLower().Trim()));

                benficiaries = benficiaries.Where(predicate);
            }

            return benficiaries;
        }
    }
}
