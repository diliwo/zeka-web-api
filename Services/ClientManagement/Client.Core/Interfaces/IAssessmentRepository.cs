using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IAssessmentRepository
    {
        int Persist(Assessment assessment);
        IQueryable<Assessment> GetAssessments();
        IQueryable<Assessment> GetAssessments(int Client);
        void SoftDelete(Assessment assessment);
        Assessment GetAssessmentById(int id);
        Assessment GetCurrentAssessment();
        IQueryable<Assessment> GetArchivedBilans();
        Task<bool> IsBilanNotFinalizd(int bilanId);
        Task<bool> AreAllAssessmentsNotFinalized(int ClientId);
    }
}