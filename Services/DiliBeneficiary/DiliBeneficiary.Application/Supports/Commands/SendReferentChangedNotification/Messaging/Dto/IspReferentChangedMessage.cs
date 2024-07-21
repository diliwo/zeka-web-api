namespace DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification.Messaging.Dto
{
    public class IspReferentChangedMessage
    {
        public string WorkerFirstName { get; set; }
        public string WorkerLastName { get; set; }
        public string WorkerSamAccountName { get; set; } = string.Empty; 
        public string BeneficiaryNiss { get; set; }
        public string BeneficiaryFirstName { get; set; } = string.Empty;
        public string BeneficiaryLastName { get; set; } = string.Empty;
        public string BeneficiaryReferenceNumber { get; set; } = string.Empty;

    }
}
