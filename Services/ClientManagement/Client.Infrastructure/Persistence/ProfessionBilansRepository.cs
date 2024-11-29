using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class ProfessionalAssessmentRepository : IProfessionalAssessmentRepository
    {
        private readonly ApplicationDbContext _context;

        public ProfessionalAssessmentRepository() { }

        public ProfessionalAssessmentRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(ProfessionalAssessment professionalAssessment)
        {
            if (professionalAssessment.Id == default(int))
            {
                _context.ProfessionalAssessments.Add(professionalAssessment);
            }
            else
            {
                _context.ProfessionalAssessments.Update(professionalAssessment);
            }
            _context.SaveChanges();
        }

        public ProfessionalAssessment Get(int professionBilanId)
        {
            return _context.ProfessionalAssessments.FirstOrDefault(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public Task<ProfessionalAssessment> GetASync(int professionBilanId)
        {
            return _context.ProfessionalAssessments.FirstOrDefaultAsync(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public IQueryable<ProfessionalAssessment> GetProfessionBilans()
        {
            return _context.ProfessionalAssessments.Where(c => c.Softdelete != true);
        }

        public IQueryable GetProfessionByBilanId(int id)
        {
            return _context.ProfessionalAssessments.Where(s => s.AssessmentId == id && s.Softdelete != true);
        }

        public void SoftDelete(ProfessionalAssessment professionalAssessment)
        {
            _context.ProfessionalAssessments.Update(professionalAssessment);
            _context.SaveChanges();
        }
    }
}