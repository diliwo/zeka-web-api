using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface ITrainingsRepository
    {
        void Persist(Training training);
        Training GetById(int id);
        IQueryable<Training> GetTrainings(string filter = "");
        void SoftDelete(Training training);
    }
}