using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class ProfessionnalExperienceRepository : RepositoryBase<ProfessionnalExperience>, IProfessionnalExperienceRepository
    {

        public ProfessionnalExperienceRepository(ApplicationDbContext context) : base(context) { }

        public ProfessionnalExperience Get(int id, bool trackChanges = false)
        {
            return FindByCondition(p => p.Id.Equals(id), trackChanges).FirstOrDefault();

        }

        public Task<ProfessionnalExperience> GetAsync(int id, bool trackChanges = false)
        {
            return FindByCondition(p => p.Equals(id), trackChanges).FirstOrDefaultAsync();
        }

        public IQueryable<ProfessionnalExperience> GetProfessionnalExperienceByBenef(int beneficiaryId, bool trackChanges = false)
        { 
            return FindByCondition(p => p.ClientId.Equals(beneficiaryId) && p.Softdelete != true, trackChanges);
        }

        public void Persist(ProfessionnalExperience professionnalExpectation)
        {
            if (professionnalExpectation.Id == default(int))
            {
                // Insert
                Create(professionnalExpectation);
            } else
            {
                // Update
                Update(professionnalExpectation);
            }
        }

        public void SoftDelete(ProfessionnalExperience professionnalExpectation)
        {
            if (professionnalExpectation.Softdelete)
            {
                professionnalExpectation.Softdelete = false;
            }
            else
            {
                professionnalExpectation.Softdelete = true;
            }

            Update(professionnalExpectation);
        }
    }
}
