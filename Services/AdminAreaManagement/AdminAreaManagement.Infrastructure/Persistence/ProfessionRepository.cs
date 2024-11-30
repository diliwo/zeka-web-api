using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class ProfessionRepository : IProfessionRepository
    {
        private readonly ApplicationDbContext _context;
        public ProfessionRepository() { }

        public ProfessionRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(Profession profession)
        {
            if (profession.Id == default(int))
            {
                _context.Professions.Add(profession);
            }
            else
            {
                _context.Professions.Update(profession);
            }
            _context.SaveChanges();
        }

        public Profession Get(int id)
        {
            var profession = _context.Professions.FirstOrDefault(s => s.Id == id);
            return profession;
        }

        public Task<Profession> GetASync(int professionId)
        {
            return _context.Professions.FirstOrDefaultAsync(c => c.Id == professionId && c.Softdelete != true);
        }

        public IQueryable<Profession> GetProfessions(string filter = "", string orderBy = "")
        {

            var professions = _context.Professions.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<Profession>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                professions = professions.Where(predicate);
            }

            return professions;
        }

        public void SoftDelete(Profession profession)
        {
            _context.Professions.Update(profession);
            _context.SaveChanges();
        }

        public void Dispose()
        {
            if (_context !=null)
            {
                _context.Dispose();
            }
        }

        public Boolean IsProfessionUnique(string name)
        {
            var foundedService = _context.Teams.FirstOrDefault(s => String.Equals(s.Name, name));

            if (foundedService == null)
            {
                return true;
            }

            return false;
        }
    }
}
