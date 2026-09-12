using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ISupportRepository
    {
        void Persist(SocialCase socialCase);
        SocialCase Get(int id);
        Task<SocialCase> GetAsync(int id);
        SocialCase GetLastSupportForClient(int id);
        Task<SocialCase> GetLastSupportForClientAsync(int id);
        IQueryable<SocialCase> GetSupports();
        IQueryable GetSupportsByClientId(int id);
        //void Delete(Support support);
        IEnumerable<SocialCase> GetSupportsByClient(int id);
        bool GetNumberOfClientSupports(int id);
        void SoftDelete(SocialCase socialCase);
        public Task<bool> DateAlreadyExists(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date, int supportId);
        public Task<bool> EndDateIsGreaterThanStartDate(int? supportId, DateTime? endDate);
        public bool isSupportForClient(int ClientId, int? supportId);
        IQueryable<MySupportDto> GetConsultantSupportsByMembership(Guid membershipId, string filter="", bool isActive = true);
        SocialCase GetWithDetails(int id);
    }
}
