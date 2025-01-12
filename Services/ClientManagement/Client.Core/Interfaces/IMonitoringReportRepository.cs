using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IMonitoringReportRepository
    {
        Task<int> Persist(MonitoringReport qMonitoringReport);
        IQueryable<MonitoringReport> getQuarterlyMonitorings(string searchText = "", bool withDeleted = false);
        IQueryable<MonitoringReport> getQuarterlyMonitoringsByClientId(int ClientId, string searchText = "", bool withDeleted = false);
        IQueryable<MonitoringReport> getQuarterlyMonitoringsByStaffMemberId(int referntId, string searchText = "", bool withDeleted = false);
        void SoftDelete(int id);
        IQueryable<MonitoringReport> GetQuarterlyMonitoringById(int id);
    }
}
