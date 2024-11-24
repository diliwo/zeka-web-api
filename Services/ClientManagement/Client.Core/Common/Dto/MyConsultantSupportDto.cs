namespace Client.Core.Common.Dto
{
    public class MyConsultantSupportDto
    {
        public int ClientId { get; set; }
        public string ClientReferenceNumber { get; set; }
        public string ClientFullName { get; set; }
        public string ClientLastName { get; set; }
        public string ClientFirstName { get; set; }
        public string ClientNiss { get; set; }
        public string ClientPhoneNumber { get; set; }
        public string LastAction { get; set; }
        public bool HasChildren { get; set; }
        public IEnumerable<String> Projects { get; set; }
        public DateTime? LastApppointmentDate { get; set; }
        public DateTime? LastEvaluationDate { get; set; }
        public string ClientNativeLanguage { get; set; }
        public string ClientContactLanguage { get; set; }
        public string Comment { get; set; }
    }
}
