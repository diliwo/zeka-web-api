using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class BeneficiaryRepository : RepositoryBase<Beneficiary>,IBeneficiaryRepository
    {

        public BeneficiaryRepository(ApplicationDbContext context) : base(context){}

        public void Persist(Beneficiary beneficiary)
        {
            if (beneficiary.Id == default(int))
            {
                // Insert
                Create(beneficiary);
            }
            else
            {
                //Update
                Update(beneficiary);
            }

        }

        public Beneficiary Get(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefault();
        }

        public Task<Beneficiary> GetSourceIdAsync(int sourceId, bool trackChanges = false)
        {
            return FindByCondition(b => b.SourceId.Equals(sourceId), trackChanges).FirstOrDefaultAsync();
        }

        public Task<Beneficiary> GetAsync(int id, bool trackChanges = false)
        {
            return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefaultAsync();
        }

        public Beneficiary GetBeneficiaryByNiss(string niss, bool trackChanges = false)
        {
            return FindByCondition(beneficiary => beneficiary.Niss.Equals(niss), trackChanges)
                .SingleOrDefault();
        }

        public Task<Beneficiary> GetBeneficiaryByNissAsync(string niss, bool trackChanges = false)
        {
            return FindByCondition(beneficiary => beneficiary.Niss.Equals(niss), trackChanges)
                .SingleOrDefaultAsync();
        }

        public IQueryable<Beneficiary> GetBeneficiaries()
        {
            return FindAll(false);
        }

        public async Task<IEnumerable<Beneficiary>> GetBeneficiariesAsync(bool trackChanges) => await FindAll(trackChanges).ToListAsync();

        public async Task<List<string>> GetBeneficiaryNissesAsync(bool trackChanges) => await FindAll(trackChanges).Select(n => n.Niss).ToListAsync();

        public IQueryable<Beneficiary> GetBeneficiariesBySearchText(string text)
        {
            var benficiaries = FindByCondition(p => p.Softdelete != true, false).AsExpandable();

            if (!string.IsNullOrEmpty(text))
            {
                var predicate = PredicateBuilder.New<Beneficiary>();

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
