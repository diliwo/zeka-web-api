using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface ISchoolRepository
    {
        void Persist(School school);
        School GetSchoolById(int schoolId);
        IQueryable<School> GetSchools(string filter = null, string orderBy = "");
        void SoftDelete(School School);
    }
}