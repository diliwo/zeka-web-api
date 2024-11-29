using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class TeamRepository : ITeamRepository
    {
        private readonly ApplicationDbContext _context;

        public TeamRepository() { }

        public TeamRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(Team service)
        {
            if (service.Id == default(int))
            {
                _context.Teams.Add(service);
            }
            else
            {
                _context.Teams.Update(service);
            }
            _context.SaveChanges();
        }

        public Team Get(int id)
        {
            var service = _context.Teams.FirstOrDefault(s => s.Id == id);
            return service;
        }

        public Boolean IsTeamUnique(string name)
        {
            var foundedService = _context.Teams.FirstOrDefault(s => String.Equals(s.Name, name));

            if (foundedService == null)
            {
                return true;
            }

            return false;
        }


        public IQueryable<Team> GetTeams(string filter = null)
        {

            var services = _context.Teams.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<Team>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.Acronym.ToLower().Contains(filter.ToLower().Trim()));

                services = services.Where(predicate);
            }

            return services;
        }

        public void SoftDelete(Team service)
        {
            _context.Teams.Update(service);
            _context.SaveChanges();
        }

        public Task<bool> ContainsStaffMembers(int teamId)
        {
            throw new NotImplementedException();
        }

        public async Task<bool> TeamHasStaffMembers(int serviceId)
        {
            var result = _context.StaffMembers.Any(r => r.TeamId == serviceId && r.Softdelete == false);
            return result;
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
