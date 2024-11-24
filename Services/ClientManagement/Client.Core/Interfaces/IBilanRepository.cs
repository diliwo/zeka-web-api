using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface IBilanRepository
    {
        int Persist(Assessment assessment);
        IQueryable<Assessment> GetBilans();
        IQueryable<Assessment> GetBilans(int Client);
        void SoftDelete(Assessment assessment);
        Assessment GetBilanById(int id);
        Assessment GetCurrentBilan();
        IQueryable<Assessment> GetArchivedBilans();
        Task<bool> IsBilanNotFinalizd(int bilanId);
        Task<bool> AreAllBilansNotFinalized(int ClientId);
    }
}