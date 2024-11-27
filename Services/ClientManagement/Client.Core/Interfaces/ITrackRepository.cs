using Client.Core.Common.Dto;
using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface ITrackRepository
    {
        void Persist(Track track);
        Track Get(int id);
        Task<Track> GetAsync(int id);
        Track GetLastTrackForClient(int id);
        Task<Track> GetLastTrackForClientAsync(int id);
        IQueryable<Track> GetTracks();
        IQueryable GetTracksByClientId(int id);
        //void Delete(Track support);
        IEnumerable<Track> GetTracksByClient(int id);
        bool GetNumberOfClientTracks(int id);
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
