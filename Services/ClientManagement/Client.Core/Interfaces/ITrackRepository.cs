using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ITrackRepository
    {
        void Persist(Support support);
        Support Get(int id);
        Task<Support> GetAsync(int id);
        Support GetLastTrackForClient(int id);
        Task<Support> GetLastTrackForClientAsync(int id);
        IQueryable<Support> GetTracks();
        IQueryable GetTracksByClientId(int id);
        //void Delete(Support support);
        IEnumerable<Support> GetTracksByClient(int id);
        bool GetNumberOfClientTracks(int id);
        void SoftDelete(Support support);
        public Task<bool> DateAlreadyExists(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date, int supportId);
        public Task<bool> EndDateIsGreaterThanStartDate(int? supportId, DateTime? endDate);
        public bool isSupportForClient(int ClientId, int? supportId);
        IQueryable<MyConsultantSupportDto> GetConsultantSupportsByUserName(string username, string filter="", bool isActive = true);
        public IQueryable<MyJobCoachSupportDto> GetJobCoachSupportsByUserName(string username, string filter = "", bool isActive = true);
        Support GetWithDetails(int id);
    }
}
