using Client.Core.Common.Dto;
using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface ISupportRepository
    {
        void Persist(Track track);
        Track Get(int id);
        Task<Track> GetAsync(int id);
        Track GetLastSupportForClient(int id);
        Task<Track> GetLastSupportForClientAsync(int id);
        IQueryable<Track> GetSupports();
        IQueryable GetSupportsByClientId(int id);
        //void Delete(Track support);
        IEnumerable<Track> GetSupportsByClient(int id);
        bool GetNumberOfClientSupports(int id);
        void SoftDelete(Track track);
        public Task<bool> DateAlreadyExists(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date, int supportId);
        public Task<bool> EndDateIsGreaterThanStartDate(int? supportId, DateTime? endDate);
        public bool isSupportForClient(int ClientId, int? supportId);
        IQueryable<MyConsultantSupportDto> GetConsultantSupportsByUserName(string username, string filter="", bool isActive = true);
        public IQueryable<MyJobCoachSupportDto> GetJobCoachSupportsByUserName(string username, string filter = "", bool isActive = true);
        Track GetWithDetails(int id);
    }
}
