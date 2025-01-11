using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;

namespace ClientManagement.Infrastructure.Persistence
{
    public class QuarterlyMonitoringRepository : IQuarterlyMonitoringRepository
    {
        private readonly ApplicationDbContext _context;
        public QuarterlyMonitoringRepository(ApplicationDbContext context)
        {
            _context = context;
        }
        
        public async Task<int> Persist(MonitoringReport qMonitoringReport)
        {
            if (qMonitoringReport.Id == default(int))
            {
                _context.QuarterlyMonitorings.Add(qMonitoringReport);
                await _context.SaveChangesAsync();
                return qMonitoringReport.Id;
            }
            else
            {
                var entity = await _context.QuarterlyMonitorings.FindAsync(qMonitoringReport.Id);
                if (entity != null)
                {
                    _context.Entry(entity).CurrentValues.SetValues(qMonitoringReport);
                    await _context.SaveChangesAsync();
                    return entity.Id;
                }
            }
            return 0;
        }

        public IQueryable<MonitoringReport> getQuarterlyMonitorings(string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => withDeleted || !q.Softdelete)
            .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                        (q.Client.LastName + " " + q.Client.FirstName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.Client.FirstName + " " + q.Client.LastName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.Client.Ssn).Contains(searchText) ||
                        (q.SocialWorker.LastName + " " + q.SocialWorker.FirstName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.SocialWorker.FirstName + " " + q.SocialWorker.LastName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        q.MonitoringAction.Action.ToUpper().Contains(searchText.ToUpper()));
        }

        public IQueryable<MonitoringReport> getQuarterlyMonitoringsByClientId(int ClientId, string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => q.ClientId == ClientId)
                .Where(q => withDeleted || !q.Softdelete)
                .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                            (q.SocialWorker.LastName + " " + q.SocialWorker.FirstName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.SocialWorker.FirstName + " " + q.SocialWorker.LastName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            q.MonitoringAction.Action.ToUpper().Contains(searchText.ToUpper()));
        }

        public IQueryable<MonitoringReport> getQuarterlyMonitoringsByStaffMemberId(int referntId, string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => q.SocialWorkerId == referntId)
                .Where(q => withDeleted || !q.Softdelete)
                .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                            (q.Client.LastName + " " + q.Client.FirstName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.Client.FirstName + " " + q.Client.LastName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.Client.Ssn).Contains(searchText) ||
                            q.MonitoringAction.Action.ToUpper().Contains(searchText.ToUpper()));
        }

        public void SoftDelete(int id)
        {
            var entity = _context.QuarterlyMonitorings.Find(id);
            if (entity != null && !entity.Softdelete)
            {
                entity.Softdelete = true;
                _context.SaveChanges();
            }
        }

        public IQueryable<MonitoringReport> GetQuarterlyMonitoringById(int id)
        {
            return _context.QuarterlyMonitorings.Where(m => m.Id == id);
        }
    }
}
