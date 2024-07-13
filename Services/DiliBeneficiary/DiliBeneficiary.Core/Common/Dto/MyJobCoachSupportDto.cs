namespace DiliBeneficiary.Core.Common.Dto
{
    public class MyJobCoachSupportDto
    {
        public int BeneficiaryId { get; set; }
        public string BeneficiaryReferenceNumber { get; set; }
        public string BeneficiaryFullName { get; set; }
        public string BeneficiaryLastName { get; set; }
        public string BeneficiaryFirstName { get; set; }
        public string BeneficiaryNiss { get; set; }
        public string BeneficiaryContactLanguage { get; set; }
        public bool HasChildren { get; set; }
        public DateTime? DateLastStartContract { get; set; }
        public DateTime? DateLastEndContract { get; set; }
        public string Workplace { get; set; }
        public DateTime? DateLastQuartelyEvaluation { get; set; }
        public string Comment { get; set;}
    }
}
