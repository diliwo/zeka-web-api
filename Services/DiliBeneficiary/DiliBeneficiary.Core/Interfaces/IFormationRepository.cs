using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IFormationRepository
    {
        void Persist(Formation formation);
        Formation GetFormationById(int formationId);
        IQueryable<Formation> GetFormations(string filter = "");
        void SoftDelete(Formation formation);
    }
}