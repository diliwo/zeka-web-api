using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ISocialWorkerRepository
    {
        void Persist(SocialWorker StaffMember);
        SocialWorker Get(int id);
        bool UserAlreadyExists(string username, int idService);
        IQueryable<SocialWorker> GetSocialWorkers(string filter = null, string orderBy = "");
        void SoftDelete(SocialWorker StaffMember);
        public Task<bool> TeamHasSocialWorkers(int serviceId);
    }
}
