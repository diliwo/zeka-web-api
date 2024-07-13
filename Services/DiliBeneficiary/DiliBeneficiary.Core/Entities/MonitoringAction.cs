using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class MonitoringAction : Entity
    {
        public string Action { get; set; }
        public virtual IList<QuarterlyMonitoring> QuarterlyMonitorings { get; private set; } = new List<QuarterlyMonitoring>();
        public MonitoringAction()
        {
        }
        public MonitoringAction(string action)
        {
            Action = action;
        }
    }
}
