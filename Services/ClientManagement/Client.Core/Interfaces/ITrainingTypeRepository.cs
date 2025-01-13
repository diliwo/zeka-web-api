using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ITrainingTypeRepository
    {
        void Persist(TrainingType type);
        TrainingType GetById(int typeId);
        IQueryable<TrainingType> GetTypes(string filter = "", string orderBy = "");
        void SoftDelete(TrainingType type);
    }
}