using System.Linq.Dynamic.Core;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class StaffMemberRepository : IStaffMemberRepository
    {
        private readonly ApplicationDbContext _context;

        public StaffMemberRepository() { }

        public StaffMemberRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(StaffMember referent)
        {
            if (referent.Id == default(int))
            {
                _context.StaffMembers.Add(referent);
            }
            else
            {
                _context.StaffMembers.Update(referent);
            }
            _context.SaveChanges();
        }

        public StaffMember Get(int id)
        {
            return _context.StaffMembers.FirstOrDefault(s => s.Id == id);
        }

        public bool UserAlreadyExists(string username, int idService)
        {
            bool result = false;
            var referent = _context.StaffMembers.FirstOrDefault(s => s.UserName == username && s.TeamId == idService && s.Softdelete != true);

            if (referent != null)
            {
                result = true;
            }

            return result;
        }

        public IQueryable<StaffMember> GetStaffMembers(string filter = null, string orderBy = "")
        {
            var referents = _context.StaffMembers.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<StaffMember>();

                predicate = predicate.Or(p => p.LastName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.FirstName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.Team.Name.ToLower().Contains(filter.ToLower().Trim()));

                referents = referents.Where(predicate);
            }

            return referents;
        }

        public async Task<bool> ServiceHasReferents(int serviceId)
        {
            var result = _context.StaffMembers.Any(r => r.TeamId == serviceId && r.Softdelete == false);
            return result;
        }

        public void SoftDelete(StaffMember Referent)
        {
            _context.StaffMembers.Update(Referent);
            _context.SaveChanges();
        }

        public void Dispose()
        {
            if (_context !=null)
            {
                _context.Dispose();
            }
        }
    }
}
