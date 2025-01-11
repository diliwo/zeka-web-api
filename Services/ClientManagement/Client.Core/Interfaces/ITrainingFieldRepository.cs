using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ITrainingFieldRepository
    {
        void Persist(TrainingField field);
        TrainingField GetById(int fieldId);
        IQueryable<TrainingField> GetFields(string filter = "", string orderBy = "");
        void SoftDelete(TrainingField field);
    }
}