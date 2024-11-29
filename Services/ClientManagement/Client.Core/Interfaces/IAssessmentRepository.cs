using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IAssessmentRepository
    {
        int Persist(Assessment assessment);
        IQueryable<Assessment> GetAssessments();
        IQueryable<Assessment> GetAssessments(int Client);
        void SoftDelete(Assessment assessment);
        Assessment GetBilanById(int id);
        Assessment GetCurrentBilan();
        IQueryable<Assessment> GetArchivedBilans();
        Task<bool> IsBilanNotFinalizd(int bilanId);
        Task<bool> AreAllBilansNotFinalized(int ClientId);
    }
}