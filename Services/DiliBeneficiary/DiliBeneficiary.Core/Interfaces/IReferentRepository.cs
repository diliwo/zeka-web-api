using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IReferentRepository
    {
        void Persist(Referent referent);
        Referent Get(int id);
        bool UserAlreadyExists(string username, int idService);
        IQueryable<Referent> GetReferents(string filter = null, string orderBy = "");
        //void Delete(Referent referent);
        void SoftDelete(Referent referent);
        public Task<bool> ServiceHasReferents(int serviceId);
    }
}
