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

        public void Persist(SchoolRegistration schoolRegistration)
        {
            if (schoolRegistration.Id == default(int))
            {
                _context.SchoolRegistrations.Add(schoolRegistration);
            }
            else
            {
                _context.SchoolRegistrations.Update(schoolRegistration);
            }
            _context.SaveChanges();
        }

        public SchoolRegistration GetRegistrationById(int registrationId)
        {
            return _context.SchoolRegistrations.FirstOrDefault(c => c.Id == registrationId && c.Softdelete != true);
        }

        public IQueryable GetResgistrationsByClientId(int id, string filter="")
        {
            var registrations = _context.SchoolRegistrations.AsNoTracking().Where(p => p.ClientId == id && p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<SchoolRegistration>();

                predicate = predicate.Or(s => s.School.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.School.Locality.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.TrainingType.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(s => s.Training.Name.ToLower().Contains(filter.ToLower().Trim()));

                registrations = registrations.Where(predicate);
            }

            return registrations;
        }

        public IEnumerable<SchoolRegistration> GetResgistrationsByClient(int id)
        {
            return _context.SchoolRegistrations.Where(s => s.ClientId.Equals(id) && s.Softdelete != true)
                    .Include(f => f.Training)
                    .ThenInclude(f => f.TrainingField)
                    .Include(s => s.School)
                    .Include(t => t.TrainingType);
        }

        public IQueryable<SchoolRegistration> GetRegistrations()
        {
            return from registrations in _context.SchoolRegistrations 
                where registrations.Softdelete != true 
                select registrations;
        }

        public void SoftDelete(SchoolRegistration schoolRegistration)
        {
            _context.SchoolRegistrations.Update(schoolRegistration);
            _context.SaveChanges();
        }
    }
}