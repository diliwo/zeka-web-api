using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IProfessionalAssessmentRepository
    {
        void Persist(ProfessionalAssessment professionalAssessment);
        ProfessionalAssessment Get(int professionBilanId);
        Task<ProfessionalAssessment> GetASync(int professionBilanId);
        IQueryable<ProfessionalAssessment> GetProfessionBilans();
        public IQueryable GetProfessionByBilanId(int id);
        void SoftDelete(ProfessionalAssessment professionalAssessment);
    }
}