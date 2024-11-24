using Client.Core.Entities;
using Client.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Client.Infrastructure.Persistence
{
    public class ProfessionBilanRepository : IProfessionBilanRepository
    {
        private readonly ApplicationDbContext _context;

        public ProfessionBilanRepository() { }

        public ProfessionBilanRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(ProfessionalAssessment professionalAssessment)
        {
            if (professionalAssessment.Id == default(int))
            {
                _context.ProfessionBilans.Add(professionalAssessment);
            }
            else
            {
                _context.ProfessionBilans.Update(professionalAssessment);
            }
            _context.SaveChanges();
        }

        public ProfessionalAssessment Get(int professionBilanId)
        {
            return _context.ProfessionBilans.FirstOrDefault(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public Task<ProfessionalAssessment> GetASync(int professionBilanId)
        {
            return _context.ProfessionBilans.FirstOrDefaultAsync(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public IQueryable<ProfessionalAssessment> GetProfessionBilans()
        {
            return _context.ProfessionBilans.Where(c => c.Softdelete != true);
        }

        public IQueryable GetProfessionByBilanId(int id)
        {
            return _context.ProfessionBilans.Where(s => s.AssessmentId == id && s.Softdelete != true);
        }

        public void SoftDelete(ProfessionalAssessment professionalAssessment)
        {
            _context.ProfessionBilans.Update(professionalAssessment);
            _context.SaveChanges();
        }
    }
}