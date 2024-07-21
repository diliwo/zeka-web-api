using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface ISchoolRegistrationRepository
    {
        void Persist(SchoolRegistration schoolRegistration);
        SchoolRegistration GetRegistrationById(int registrationId);
        IEnumerable<SchoolRegistration> GetResgistrationsByBeneficiary(int id);
        IQueryable<SchoolRegistration> GetRegistrations();
        IQueryable GetResgistrationsByBeneficiaryId(int id, string filter = "");
        void SoftDelete(SchoolRegistration schoolRegistration);
    }
}