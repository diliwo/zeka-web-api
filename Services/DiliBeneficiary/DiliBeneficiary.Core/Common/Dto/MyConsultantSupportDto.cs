namespace DiliBeneficiary.Core.Common.Dto
{
    public class MyConsultantSupportDto
    {
        public int BeneficiaryId { get; set; }
        public string BeneficiaryReferenceNumber { get; set; }
        public string BeneficiaryFullName { get; set; }
        public string BeneficiaryLastName { get; set; }
        public string BeneficiaryFirstName { get; set; }
        public string BeneficiaryNiss { get; set; }
        public string BeneficiaryPhoneNumber { get; set; }
        public string LastAction { get; set; }
        public bool HasChildren { get; set; }
        public IEnumerable<String> Projects { get; set; }
        public DateTime? LastApppointmentDate { get; set; }
        public DateTime? LastEvaluationDate { get; set; }
        public string BeneficiaryNativeLanguage { get; set; }
        public string BeneficiaryContactLanguage { get; set; }
        public string Comment { get; set; }
    }
}
