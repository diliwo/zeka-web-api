using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface ICityRepository
    {
        void Persist(City nationality);
        City GetById(int id);
        void SoftDelete(City city);
    }
}
