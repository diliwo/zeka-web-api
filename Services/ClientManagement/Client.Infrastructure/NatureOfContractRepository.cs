using System.Linq;
using System.Threading.Tasks;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using ClientManagement.Infrastructure.Persistence;
using LinqKit;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Cpas.Isp.Infrastructure.Persistence;

public class NatureOfContractRepository : RepositoryBase<NatureOfContract>, INatureOfContractRepository {

    public NatureOfContractRepository(ApplicationDbContext context) : base(context) { }

    public void Persist(NatureOfContract type)
    {
        if (type.Id == default(int))
        {
            Create(type);
        }
        else
        {
            Update(type);
        }
    }

    public NatureOfContract Get(int id, bool trackChanges = false)
    {
        return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefault();
    }

    public Task<NatureOfContract> GetAsync(int id, bool trackChanges = false)
    {
        return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefaultAsync();
    }

    public IQueryable<NatureOfContract> GetContracts(string filter = "")
    {
        var contracts = FindByCondition(p => p.Softdelete != true, false).AsExpandable();

        if (!string.IsNullOrEmpty(filter))
        {
            var predicate = PredicateBuilder.New<NatureOfContract>();

            predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));

            contracts = contracts.Where(predicate);
        }

        return contracts;
    }

    public void SoftDelete(NatureOfContract contract)
    {
        if (contract.Softdelete)
        {
            contract.Softdelete = false;
        }
        else
        {
            contract.Softdelete = true;
        }

        Update(contract);
    }
}