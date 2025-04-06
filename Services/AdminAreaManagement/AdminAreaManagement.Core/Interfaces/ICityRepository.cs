using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface ICityRepository
    {
        void Persist(City nationality);
        City GetById(int id);
        IQueryable<City> GetPlaces(string filter = "");
        void SoftDelete(City city);
    }
}