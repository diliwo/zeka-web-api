using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Application.Models
{
    public class BilanReportDocumentModel
    {
        public string CpasLogo { get; set; }
        public string CpasNameFr { get; set; }
        public string CpasNameNl { get; set; }
        public string CpasZip { get; set; }
        public string CpasAdresse { get; set; }
        public string CallCenter { get; set; }
        public string EmailFr { get; set; }
        public string EmailNl { get; set; }
        public Bilan Bilan { get; set; }
        public Beneficiary Beneficiary { get; set; }
        public List<Support> Supports { get; set; }
        public List<SchoolRegistration> SchoolRegistrations { get; set; } = new List<SchoolRegistration>();
        public List<ProfessionnalExperience> ProfessionalExperiences { get; set; } = new List<ProfessionnalExperience>();

    }
}
