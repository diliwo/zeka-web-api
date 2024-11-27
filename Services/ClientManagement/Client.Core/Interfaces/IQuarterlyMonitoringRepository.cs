using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface IQuarterlyMonitoringRepository
    {
        Task<int> Persist(QuarterlyMonitoring qMonitoring);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitorings(string searchText = "", bool withDeleted = false);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByClientId(int ClientId, string searchText = "", bool withDeleted = false);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByStaffId(int referntId, string searchText = "", bool withDeleted = false);
        void SoftDelete(int id);
        IQueryable<QuarterlyMonitoring> GetQuarterlyMonitoringById(int id);
    }
}
