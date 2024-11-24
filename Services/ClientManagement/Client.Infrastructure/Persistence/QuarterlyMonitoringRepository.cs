using Client.Core.Entities;
using Client.Core.Interfaces;

namespace Client.Infrastructure.Persistence
{
    public class QuarterlyMonitoringRepository : IQuarterlyMonitoringRepository
    {
        private readonly ApplicationDbContext _context;
        public QuarterlyMonitoringRepository(ApplicationDbContext context)
        {
            _context = context;
        }
        
        public async Task<int> Persist(QuarterlyMonitoring qMonitoring)
        {
            if (qMonitoring.Id == default(int))
            {
                _context.QuarterlyMonitorings.Add(qMonitoring);
                await _context.SaveChangesAsync();
                return qMonitoring.Id;
            }
            else
            {
                var entity = await _context.QuarterlyMonitorings.FindAsync(qMonitoring.Id);
                if (entity != null)
                {
                    _context.Entry(entity).CurrentValues.SetValues(qMonitoring);
                    await _context.SaveChangesAsync();
                    return entity.Id;
                }
            }
            return 0;
        }

        public IQueryable<QuarterlyMonitoring> getQuarterlyMonitorings(string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => withDeleted || !q.Softdelete)
            .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                        (q.Client.LastName + " " + q.Client.FirstName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.Client.FirstName + " " + q.Client.LastName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.Client.Niss).Contains(searchText) ||
                        (q.Staff.LastName + " " + q.Staff.FirstName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        (q.Staff.FirstName + " " + q.Staff.LastName).ToUpper()
                        .Contains(searchText.ToUpper()) ||
                        q.MonitoringAction.Action.ToUpper().Contains(searchText.ToUpper()));
        }

        public IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByClientId(int ClientId, string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => q.ClientId == ClientId)
                .Where(q => withDeleted || !q.Softdelete)
                .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                            (q.Staff.LastName + " " + q.Staff.FirstName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.Staff.FirstName + " " + q.Staff.LastName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            q.MonitoringAction.Action.ToUpper().Contains(searchText.ToUpper()));
        }

        public IQueryable<QuarterlyMonitoring> getQuarterlyMonitoringsByStaffId(int referntId, string searchText = "", bool withDeleted = false)
        {
            return _context.QuarterlyMonitorings
                .Where(q => q.StaffId == referntId)
                .Where(q => withDeleted || !q.Softdelete)
                .Where(q => String.IsNullOrWhiteSpace(searchText) ||
                            (q.Client.LastName + " " + q.Client.FirstName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.Client.FirstName + " " + q.Client.LastName).ToUpper()
                            .Contains(searchText.ToUpper()) ||
                            (q.Client.Niss).Contains(searchText) ||
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

        public IQueryable<QuarterlyMonitoring> GetQuarterlyMonitoringById(int id)
        {
            return _context.QuarterlyMonitorings.Where(m => m.Id == id);
        }
    }
}
