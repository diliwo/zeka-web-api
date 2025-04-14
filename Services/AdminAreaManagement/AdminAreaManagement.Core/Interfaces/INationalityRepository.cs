using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces;

public interface INationalityRepository
{
    void Persist(Nationality nationality);
    Nationality GetById(int id);
    IQueryable<Nationality> GetNationalities(string filter = "");
    void SoftDelete(Nationality nationality);
}