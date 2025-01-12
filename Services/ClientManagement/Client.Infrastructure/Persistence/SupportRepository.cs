using System.Linq.Dynamic.Core;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class SupportRepository : ISupportRepository
    {
        private readonly ApplicationDbContext _context;
        public SupportRepository() { }

        public SupportRepository(
            ApplicationDbContext context
        )
        {
            _context = context;
        }

        public void Persist(Support support)
        {
            if (support.Id == default(int))
            {
                if (GetNumberOfClientSupports(support.Client.Id))
                {
                    var previousSupport = GetLastSupportForClient(support.Client.Id);

                    if (!previousSupport.EndDate.HasValue)
                    {
                        previousSupport.EndDate = support.StartDate.AddDays(-1);

                        _context.Supports.Update(previousSupport);
                    }
                }
                _context.Supports.Add(support);
            }
            else
            {
                _context.Supports.Update(support);
            }

            _context.SaveChanges();
        }

        public Support Get(int id)
        {
            return _context.Supports.FirstOrDefault(s => s.Id == id);
        }

        public async Task<Support> GetAsync(int id)
        {
            return await _context.Supports.FirstOrDefaultAsync(s => s.Id == id);
        }

        public Support GetLastSupportForClient(int id)
        {
            return _context.Supports.Where(b =>b.ClientId == id && b.Softdelete != true).OrderBy(s => s.StartDate).Last();
        }

        public async Task<Support> GetLastSupportForClientAsync(int id)
        {
            return await _context.Supports.Where(b => b.ClientId == id && b.Softdelete != true).OrderBy(s => s.StartDate).LastAsync();
        }

        public IQueryable<Support> GetSupports()
        {
            return _context.Supports.Where(s => s.Softdelete != true);
        }

        public IQueryable GetSupportsByClientId(int id)
        {
            return _context.Supports.Where(s => s.ClientId == id && s.Softdelete != true);
        }

        public IEnumerable<Support> GetSupportsByClient(int id)
        {
            return _context.Supports.Where(s => s.ClientId == id && s.Softdelete != true)
                .Include(s => s.SocialWorker);
        }


        public bool isSupportForClient(int ClientId, int? supportId)
        {
            bool result = false;
            var supports = _context.Supports.Where(s => s.ClientId == ClientId);

            if (supports.Any(s => s.Id == supportId))
            {
                result = true;
            }

            return result;
        }

        public void SoftDelete(Support support)
        {
            if (support.Softdelete)
            {
                support.Softdelete = false;
            }
            else
            {
                support.Softdelete = true;
            }

            _context.Supports.Update(support);

            _context.SaveChanges();
        }

        public async Task<bool> DateAlreadyExists(int ClientId,DateTime date)
        {
            var benefSupports = _context.Supports.Where(b => b.ClientId == ClientId);

            if (benefSupports == null)
            {
                return false;
            }

            return benefSupports.Any(s => DateTime.Compare(s.StartDate, date) == 0 && s.Softdelete != true);
        }

        public async Task<bool> EndDateIsGreaterThanStartDate(int? supportId,DateTime? endDate)
        {
            var support = _context.Supports.SingleOrDefault(b => b.Id == supportId);

            if (support == null)
            {
                return false;
            }

            var result = DateTime.Compare((DateTime) endDate.Value.Date,support.StartDate.Date) >= 0;

            return result;
        }

        public async Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date)
        {
            var benefSupports = await _context.Supports.Where(b => b.Softdelete != true && b.ClientId == ClientId).ToListAsync(); ;

            return await AreDateConsistent(benefSupports, date);
        }

        public async Task<bool> DateIsEarlierThanExistingDates(int ClientId, DateTime date, int supportId)
        {
            var benefSupports = await _context.Supports.Where(b => b.Softdelete != true && b.ClientId == ClientId && b.Id != supportId).ToListAsync();

            return await AreDateConsistent(benefSupports, date);
        }

        private async Task<bool> AreDateConsistent(List<Support> supports, DateTime date)
        {
            bool result = false;

            if (supports == null)
            {
                return result;
            }

            var activeSupport = supports.Find(s => s.Softdelete != true && s.EndDate == null);

            if (activeSupport is not null && DateTime.Compare(date, activeSupport.StartDate) <= 0)
            {
                result = true;
            }
            else 
            {
                result = supports.Any(s => s.Softdelete != true && DateTime.Compare(date, (s.EndDate != null) ? (DateTime) s.EndDate : DateTime.MinValue) <= 0);
            }


            return result;
        }

        public bool GetNumberOfClientSupports(int id)
        {
            return _context.Supports.Any(b =>b.ClientId == id && b.Softdelete != true);
        }

        public IQueryable<MySupportDto>GetConsultantSupportsByUserName(string username, string filter = "", bool isActive = true)
        {
            var today = DateTime.Today;
            var query = _context.Supports
                .Where(s => !s.Softdelete
                            && (Equals(s.SocialWorker.UserName,username))
                            && (!isActive || ((s.StartDate <= today && s.EndDate > today) || (s.StartDate <= today && s.EndDate == null))))
                .Select(x => new MySupportDto()
                {
                    ClientId = x.ClientId,
                    ClientReferenceNumber = x.Client.ReferenceNumber,
                    ClientFullName = $"{x.Client.LastName} {x.Client.FirstName}",
                    ClientLastName = x.Client.LastName,
                    ClientFirstName = x.Client.FirstName,
                    ClientNiss = x.Client.Ssn,
                    ClientPhoneNumber = x.Client.Phone.PhoneNumber,
                    Projects = x.Client.Assessments.OrderBy(b => b.Created).Last(b => !b.Softdelete).BilanProfessions.Select(p => p.Profession.Name),
                    LastAction = ((DateTime.Compare(x.Client.SchoolRegistrations.OrderBy(s => s.Created).Last(s => !s.Softdelete).Created,
                                      x.Client.ProfessionnalExpectations.OrderBy(c => c.Created).Last(c => !c.Softdelete).Created) >= 0))
                        ? $"[Training] : {x.Client.SchoolRegistrations.OrderBy(s => s.Created).Last(s => !s.Softdelete).Training.Name}"
                        : $"[Job] : {x.Client.ProfessionnalExpectations.OrderBy(c => c.Created).Last(c => !c.Softdelete).CompanyName}",
                    HasChildren = x.Client.HasChildren,
                    ClientContactLanguage = x.Client.ContactLanguage.SpokenLanguage,
                    ClientNativeLanguage = x.Client.NativeLanguage.SpokenLanguage,
                    Comment = x.Note
                });
            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<MySupportDto>();
       
                predicate = predicate.Or(p => p.ClientLastName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.ClientFirstName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.ClientReferenceNumber.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.Projects.Any(e => e.ToLower().Contains(filter.ToLower().Trim())));

                query = query.Where(predicate);
            }

            return query;
        }

        private string ConvertBilanJobsToString(IList<ProfessionalAssessment> jobs)
        {
            var result = "";
            foreach (var job in jobs)
            {
                result += job.Profession.Name + "\n";
            }
            return result;
        }
        public Support GetWithDetails(int id)
        {
            return _context.Supports.
                Include(s => s.Client)
                .Include(s => s.SocialWorker)
                .FirstOrDefault(s => s.Id == id);
        }
    }
}
