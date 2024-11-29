using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ISchoolRegistrationRepository
    {
        void Persist(SchoolRegistration schoolRegistration);
        SchoolRegistration GetRegistrationById(int registrationId);
        IEnumerable<SchoolRegistration> GetResgistrationsByClient(int id);
        IQueryable<SchoolRegistration> GetRegistrations();
        IQueryable GetResgistrationsByClientId(int id, string filter = "");
        void SoftDelete(SchoolRegistration schoolRegistration);
    }
}