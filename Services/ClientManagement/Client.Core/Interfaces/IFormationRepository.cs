using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface ITrainingRepository
    {
        void Persist(Training training);
        Training GetTrainingById(int TrainingId);
        IQueryable<Training> GetTrainings(string filter = "");
        void SoftDelete(Training training);
    }
}