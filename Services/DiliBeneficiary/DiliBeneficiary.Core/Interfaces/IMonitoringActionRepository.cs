using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IMonitoringActionRepository
    {
        void Persist(MonitoringAction monitoringAction);
        IQueryable<MonitoringAction> getMonitoringActions(string searchText = "", bool withDeleted = false);
        void SoftDelete(int id);
        IQueryable<MonitoringAction> GetMonitoringActionById(int id);
    }
}
