using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class AssessmentRepository : IAssessmentRepository
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
                _context.Assessments.Add(assessment);
            }
            else
            {
                _context.Assessments.Update(assessment);
            }
            _context.SaveChanges();

            return assessment.Id;
        }

        public IQueryable<Assessment> GetAssessments()
        {
            return from assessments in _context.Visible<ClientManagement.Core.Entities.Assessment>() where assessments.Softdelete != true select assessments;
        }

        public IQueryable<Assessment> GetAssessments(int ClientId)
        {
            return from assessments in _context.Visible<ClientManagement.Core.Entities.Assessment>()
                where (assessments.ClientId == ClientId && assessments.Softdelete != true) select assessments;
        }

        public Assessment GetAssessmentById(int id)
        {
            return _context.Visible<ClientManagement.Core.Entities.Assessment>().Include(b => b.BilanProfessions).ThenInclude(bp => bp.Profession).FirstOrDefault(s => s.Id == id);
        }

        public Assessment GetCurrentAssessment()
        {
            return _context.Visible<ClientManagement.Core.Entities.Assessment>().FirstOrDefault(b => b.IsFinalized != true);
        }

        public IQueryable<Assessment> GetArchivedBilans()
        {
            return from bilans in _context.Visible<ClientManagement.Core.Entities.Assessment>() where bilans.IsFinalized != false select bilans;
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

            _context.Assessments.Update(assessment);
            _context.SaveChanges();
        }

        public async Task<bool> IsBilanNotFinalizd(int bilanId)
        {
            var bilan = _context.Visible<ClientManagement.Core.Entities.Assessment>().SingleOrDefaultAsync(b => b.Id == bilanId);

            if (bilan.Result == null)
            {
                return false;
            }

            return !bilan.Result.IsFinalized ? true : false;
        }

        public async Task<bool> AreAllAssessmentsNotFinalized(int ClientId)
        {
            var bilans = _context.Visible<ClientManagement.Core.Entities.Assessment>().Where(b => b.ClientId == ClientId && b.Softdelete != true);

            if (bilans == null)
            {
                return false;
            }

            return !bilans.Any(s => s.IsFinalized == false);
        }
    }
}
