using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IQuarterlyMonitoringRepository
    {
        Task<int> Persist(QuarterlyMonitoring qMonitoring);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitorings(string searchText = "", bool withDeleted = false);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByBeneficiaryId(int beneficiaryId, string searchText = "", bool withDeleted = false);
        IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByReferentId(int referntId, string searchText = "", bool withDeleted = false);
        void SoftDelete(int id);
        IQueryable<QuarterlyMonitoring> GetQuarterlyMonitoringById(int id);
    }
}
