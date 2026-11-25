namespace ClientManagement.Application.QuarterlyMonitorings.Common
{
    public class QuarterlyMonitoringDto
    {
        public int QMonitoringId { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; }
        public string ClientLastName { get; set; }
        public string ClientFirstName { get; set; }
        public string ClientNiss { get; set; }
        public string ClientDossier { get; set; }
        public int StaffMemberId { get; set; }
        public string StaffMemberName { get; set; }
        public string StaffMemberLastName { get; set; }
        public string StaffMemberFirstName { get; set; }
        public int MonitoringActionId { get; set; }
        public string MonitoringActionLabel { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }
        public string Quarter { get; set; }
        public bool Softdelete { get; set; }
    }
}
