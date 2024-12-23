using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class SchoolRepository : ISchoolRepository
    {
        private readonly ApplicationDbContext _context;


        public SchoolRepository(){}

        public SchoolRepository(ApplicationDbContext context)
        {
            _context = context;

        }

        public void Persist(School school)
        {
            if (school.Id == default(int))
            {
                _context.Schools.Add(school);
            }
            else
            {
                _context.Schools.Update(school);
            }
            _context.SaveChanges();
        }

        public School GetSchoolById(int schoolId)
        {
            return _context.Schools.FirstOrDefault(s => s.Id == schoolId);
        }

        public IQueryable<School> GetSchools(string filter = "", string orderBy = "")
        {
            var schools = Extensions.AsExpandable(_context.Schools.AsNoTracking()).Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<School>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.Locality.ToLower().Contains(filter.ToLower().Trim()));

                schools = schools.Where(predicate);
            }

            return schools;
        }

        public void SoftDelete(School school)
        {
            _context.Schools.Update(school);
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