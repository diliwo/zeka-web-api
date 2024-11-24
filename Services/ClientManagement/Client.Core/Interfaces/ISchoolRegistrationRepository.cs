using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface ISchoolRegistrationRepository
    {
        void Persist(SchoolEnrollment schoolEnrollment);
        SchoolEnrollment GetRegistrationById(int registrationId);
        IEnumerable<SchoolEnrollment> GetResgistrationsByClient(int id);
        IQueryable<SchoolEnrollment> GetRegistrations();
        IQueryable GetResgistrationsByClientId(int id, string filter = "");
        void SoftDelete(SchoolEnrollment schoolEnrollment);
    }
}