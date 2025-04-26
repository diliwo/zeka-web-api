using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;

namespace ClientManagement.Infrastructure.Persistence;

public class SocialWorkerRepository : RepositoryBase<SocialWorker>, ISocialWorkerRepository
{
    public SocialWorkerRepository(ApplicationDbContext context) : base(context) { }

    public void Persist(SocialWorker socialWorker)
    {
        if (socialWorker.Id == default(int))
        {
            Create(socialWorker);
        }
        else
        {
            Update(socialWorker);
        }
    }

    public SocialWorker Get(int id, bool trackChanges = false)
    {
        return FindByCondition(b => b.Id.Equals(id), trackChanges).FirstOrDefault();
    }
}