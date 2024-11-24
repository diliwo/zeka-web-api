using Client.Core.Entities;
using Client.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Client.Infrastructure.Persistence
{
    public class AssessmentRepository : IBilanRepository
    {
        private readonly ApplicationDbContext _context;

        public AssessmentRepository(ApplicationDbContext context)
        {
            _context = context;
        }


        public int Persist(Assessment assessment)
        {
            if (assessment.Id == default(int))
            {
                _context.Bilans.Add(assessment);
            }
            else
            {
                _context.Bilans.Update(assessment);
            }
            _context.SaveChanges();

            return assessment.Id;
        }

        public IQueryable<Assessment> GetBilans()
        {
            return from bilans in _context.Bilans where bilans.Softdelete != true select bilans;
        }

        public IQueryable<Assessment> GetBilans(int ClientId)
        {
            return from bilans in _context.Bilans 
                where (bilans.ClientId == ClientId && bilans.Softdelete != true) select bilans;
        }

        public Assessment GetBilanById(int id)
        {
            return _context.Bilans.Include(b => b.BilanProfessions).ThenInclude(bp => bp.Profession).FirstOrDefault(s => s.Id == id);
        }

        public Assessment GetCurrentBilan()
        {
            return _context.Bilans.FirstOrDefault(b => b.IsFinalized != true);
        }

        public IQueryable<Assessment> GetArchivedBilans()
        {
            return from bilans in _context.Bilans where bilans.IsFinalized != false select bilans;
        }

        public void SoftDelete(Assessment assessment)
        {
            if (assessment.Softdelete)
            {
                assessment.Softdelete = false;
            }
            else
            {
                assessment.Softdelete = true;
            }

            foreach (var bilanProfession in assessment.BilanProfessions)
            {
                bilanProfession.AssessmentId = bilanProfession.AssessmentId - 1;
            }

            _context.Bilans.Update(assessment);
            _context.SaveChanges();     
        }

        public async Task<bool> IsBilanNotFinalizd(int bilanId)
        {
            var bilan = _context.Bilans.SingleOrDefaultAsync(b => b.Id == bilanId);

            if (bilan.Result == null)
            {
                return false;
            }

            return !bilan.Result.IsFinalized ? true : false;
        }

        public async Task<bool> AreAllBilansNotFinalized(int ClientId)
        {
            var bilans = _context.Bilans.Where(b => b.ClientId == ClientId && b.Softdelete != true);

            if (bilans == null)
            {
                return false;
            }

            return !bilans.Any(s => s.IsFinalized == false);
        }
    }
}