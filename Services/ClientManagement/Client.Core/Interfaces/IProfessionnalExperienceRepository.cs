using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IProfessionnalExperienceRepository
    {
        void Persist(ProfessionnalExperience ProfessionnalExpectation);
        ProfessionnalExperience Get(int id, bool trackChanges = false);
        public Task<ProfessionnalExperience> GetAsync(int id, bool trackChanges = false);
        IQueryable<ProfessionnalExperience> GetProfessionnalExperienceByBenef(int beneficiaryId, bool trackChanges = false);
        void SoftDelete(ProfessionnalExperience ProfessionnalExpectation);
    }
}
