using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IBilanRepository
    {
        int Persist(Bilan bilan);
        IQueryable<Bilan> GetBilans();
        IQueryable<Bilan> GetBilans(int beneficiary);
        void SoftDelete(Bilan bilan);
        Bilan GetBilanById(int id);
        Bilan GetCurrentBilan();
        IQueryable<Bilan> GetArchivedBilans();
        Task<bool> IsBilanNotFinalizd(int bilanId);
        Task<bool> AreAllBilansNotFinalized(int beneficiaryId);
    }
}