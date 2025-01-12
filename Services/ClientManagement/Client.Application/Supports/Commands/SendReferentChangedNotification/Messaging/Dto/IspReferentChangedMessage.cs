namespace ClientManagement.Application.Supports.Commands.SendReferentChangedNotification.Messaging.Dto
{
    public class IspStaffMemberChangedMessage
    {
        public string WorkerFirstName { get; set; }
        public string WorkerLastName { get; set; }
        public string WorkerSamAccountName { get; set; } = string.Empty; 
        public string ClientNiss { get; set; }
        public string ClientFirstName { get; set; } = string.Empty;
        public string ClientLastName { get; set; } = string.Empty;
        public string ClientReferenceNumber { get; set; } = string.Empty;

    }
}
