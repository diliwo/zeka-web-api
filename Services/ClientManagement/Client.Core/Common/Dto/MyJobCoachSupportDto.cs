namespace Client.Core.Common.Dto
{
    public class MyJobCoachSupportDto
    {
        public int ClientId { get; set; }
        public string ClientReferenceNumber { get; set; }
        public string ClientFullName { get; set; }
        public string ClientLastName { get; set; }
        public string ClientFirstName { get; set; }
        public string ClientNiss { get; set; }
        public string ClientContactLanguage { get; set; }
        public bool HasChildren { get; set; }
        public DateTime? DateLastStartContract { get; set; }
        public DateTime? DateLastEndContract { get; set; }
        public string Workplace { get; set; }
        public DateTime? DateLastQuartelyEvaluation { get; set; }
        public string Comment { get; set;}
    }
}
