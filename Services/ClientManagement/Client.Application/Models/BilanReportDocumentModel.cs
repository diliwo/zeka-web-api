using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Models
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
        public Assessment Assessment { get; set; }
        public Client Client { get; set; }
        public List<Track> Supports { get; set; }
        public List<SchoolRegistration> SchoolRegistrations { get; set; } = new List<SchoolRegistration>();
    }
}
