using Client.Core.Entities;
using Client.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace Client.Infrastructure.Persistence
{
    public class SchoolRegistrationRepository : ISchoolRegistrationRepository
    {
        private readonly ApplicationDbContext _context;

        public SchoolRegistrationRepository(){}

        public SchoolRegistrationRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(SchoolEnrollment schoolEnrollment)
        {
            if (schoolEnrollment.Id == default(int))
            {
                _context.SchoolRegistrations.Add(schoolEnrollment);
            }
            else
            {
                _context.SchoolRegistrations.Update(schoolEnrollment);
            }
            _context.SaveChanges();
        }

        public SchoolEnrollment GetRegistrationById(int registrationId)
        {
            return _context.SchoolRegistrations.FirstOrDefault(c => c.Id == registrationId && c.Softdelete != true);
        }

        public IQueryable GetResgistrationsByClientId(int id, string filter="")
        {
            var registrations = _context.SchoolRegistrations.AsNoTracking().Where(p => p.ClientId == id && p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<SchoolEnrollment>();

                predicate = predicate.Or(s => s.School.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.School.Locality.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.TrainingType.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.Training.Name.ToLower().Contains(filter.ToLower().Trim()));

                registrations = registrations.Where(predicate);
            }

            return registrations;
        }

        public IEnumerable<SchoolEnrollment> GetResgistrationsByClient(int id)
        {
            return _context.SchoolRegistrations.Where(s => s.ClientId.Equals(id) && s.Softdelete != true)
                    .Include(f => f.Training)
                    .ThenInclude(f => f.TrainingField)
                    .Include(s => s.School)
                    .Include(t => t.TrainingType);
        }

        public IQueryable<SchoolEnrollment> GetRegistrations()
        {
            return from registrations in _context.SchoolRegistrations 
                where registrations.Softdelete != true 
                select registrations;
        }

        public void SoftDelete(SchoolEnrollment schoolEnrollment)
        {
            _context.SchoolRegistrations.Update(schoolEnrollment);
            _context.SaveChanges();
        }
    }
}