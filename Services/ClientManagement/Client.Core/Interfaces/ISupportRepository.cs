using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ISupportRepository
    {
        void Persist(Support support);
        Support Get(int id);
        Task<Support> GetAsync(int id);
        Support GetLastSupportForClient(int id);
        Task<Support> GetLastSupportForClientAsync(int id);
        IQueryable<Support> GetSupports();
        IQueryable GetSupportsByClientId(int id);
        //void Delete(Support support);
        IEnumerable<Support> GetSupportsByClient(int id);
        bool GetNumberOfClientSupports(int id);
        void SoftDelete(Support support);
        public Task<bool> DateAlreadyExists(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date, int supportId);
        public Task<bool> EndDateIsGreaterThanStartDate(int? supportId, DateTime? endDate);
        public bool isSupportForClient(int ClientId, int? supportId);
        IQueryable<MySupportDto> GetConsultantSupportsByUserName(string username, string filter="", bool isActive = true);
        Support GetWithDetails(int id);
    }
}
