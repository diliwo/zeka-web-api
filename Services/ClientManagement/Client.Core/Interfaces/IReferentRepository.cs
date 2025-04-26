using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ISocialWorkerRepository
    {
        void Persist(SocialWorker socialWorker);

        SocialWorker Get(int id, bool trackChanges = false);
    }
}
