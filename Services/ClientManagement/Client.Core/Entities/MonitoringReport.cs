using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class MonitoringReport : Entity
    {
        public MonitoringReport()
        {
        }

        public int ClientId { get; set; }
        public Client Client { get; set; }
        public int SocialWorkerId { get; set; }
        public SocialWorker SocialWorker { get; set; }
        public int MonitoringActionId { get; set; }
        public MonitoringAction MonitoringAction { get; set; }
        public DateTime ActionDate { get; set; } = DateTime.Now;
        public string ActionComment { get; set; } = string.Empty;
        private string _quarter;
        public string Period
        {
            get => ActionDate.Year.ToString() + "Q" + ((ActionDate.Month - 1) / 3 + 1).ToString();
            set => _quarter = value;
        }

        public MonitoringReport(Client client, SocialWorker StaffMember, MonitoringAction monitoringAction,
            DateTime actionDate, string actionComment)
        {
            Client = client;
            StaffMember = StaffMember;
            MonitoringAction = monitoringAction;
            ActionDate = actionDate;
            ActionComment = actionComment;
        }

        public MonitoringReport(int ClientId, int StaffMemberId, int monitoringActionId, DateTime actionDate,
            string actionComment)
        {
            ClientId = ClientId;
            StaffMemberId = StaffMemberId;
            MonitoringActionId = monitoringActionId;
            ActionDate = actionDate;
            ActionComment = actionComment;

        }
    }
}
