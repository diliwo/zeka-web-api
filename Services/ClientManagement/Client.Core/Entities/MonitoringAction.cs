using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class MonitoringAction : Entity
    {
        public string Action { get; set; }
        public virtual IList<MonitoringReport> MonitoringReports { get; private set; } = new List<MonitoringReport>();
        public MonitoringAction()
        {
        }
        public MonitoringAction(string action)
        {
            Action = action;
        }
    }
}
