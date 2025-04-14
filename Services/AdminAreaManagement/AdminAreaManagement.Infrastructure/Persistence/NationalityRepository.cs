using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class NationalityRepository : INationalityRepository
    {
        private readonly ApplicationDbContext _context;
        public NationalityRepository() { }

        public NationalityRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(Nationality nationality)
        {
            if (nationality.Id == default(int))
            {
                _context.Nationalities.Add(nationality);
            }
            else
            {
                _context.Nationalities.Update(nationality);
            }
            _context.SaveChanges();
        }

        public Nationality GetById(int id)
        {
            var nationality = _context.Nationalities.FirstOrDefault(s => s.Id == id);
            return nationality;
        }

        public Task<Nationality> GetASync(int nationalityId)
        {
            return _context.Nationalities.FirstOrDefaultAsync(c => c.Id == nationalityId && c.Softdelete != true);
        }

        public IQueryable<Nationality> GetNationalities(string filter = "")
        {

            var professions = _context.Nationalities.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<Nationality>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                professions = professions.Where(predicate);
            }

            return professions;
        }

        public async void SoftDelete(Nationality nationality)
        {
            _context.Nationalities.Update(nationality);
           await _context.SaveChangesAsync();
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
