namespace DiliBeneficiary.Application.Beneficiaries.Commands.SociabiliDBChangeBeneficiaryMessage.Message
{

    public class SociabiliDBChangeBeneficiaryMessage
    {
        public string messageId { get; set; }
        public string conversationId { get; set; }
        public DateTime sentTime { get; set; }
        public SociabiliDBChangeBeneficiary message { get; set; }
    }

    public class SociabiliDBChangeBeneficiary
    {
        public Guid CorrelationId { get; set; }
        public SociabiliDBChange DBChange { get; set; }
    }

    public class SociabiliDBChange
    {
        public string Id { get; set; }
        public string Date { get; set; }
        public string PreviousValue { get; set; }
        public string CurrentValue { get; set; }
        public string TableName { get; set; }
        public string FieldName { get; set; }
        public string IdTarget { get; set; }
    }

}
