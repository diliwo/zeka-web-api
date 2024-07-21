using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class MonitoringActionRepository : IMonitoringActionRepository
    {
        private readonly ApplicationDbContext _context;
        public MonitoringActionRepository() { }
        public MonitoringActionRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(MonitoringAction monitoringAction)
        {
            if (monitoringAction.Id == default)
            {
                _context.MonitoringActions.Add(monitoringAction);
            }
            else
            {
                var entity = _context.MonitoringActions.Find(monitoringAction.Id);
                if (entity != null)
                {
                    _context.Entry(entity).CurrentValues.SetValues(monitoringAction);
                }
            }
            _context.SaveChanges();
        }


        public IQueryable<MonitoringAction> getMonitoringActions(string searchText = "", bool withDeleted = false)
        {
            return _context.MonitoringActions
                .Where(m => withDeleted || !m.Softdelete)
                .Where(m => string.IsNullOrWhiteSpace(searchText) || m.Action.Contains(searchText));
        }

        public void SoftDelete(int id)
        {
            var entity = _context.MonitoringActions.Find(id);
            if (entity != null && !entity.Softdelete)
            {
                entity.Softdelete = true;
                _context.SaveChanges();
            }
        }

        public IQueryable<MonitoringAction> GetMonitoringActionById(int id)
        {
            return _context.MonitoringActions.Where(m => m.Id == id && !m.Softdelete);
        }
    }
}
