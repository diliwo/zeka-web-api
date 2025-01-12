using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Models
{
    public class AssessmentReportDocumentModel
    {
        public string Logo { get; set; }
        public string Name { get; set; }
        public string ZipCode { get; set; }
        public string Address { get; set; }
        public string PhoneNumber { get; set; }
        public string Email { get; set; }
        public Assessment Assessment { get; set; }
        public Client Client { get; set; }
        public List<Support> Supports { get; set; }
        public List<SchoolRegistration> SchoolRegistrations { get; set; } = new List<SchoolRegistration>();
        public List<ProfessionnalExperience> ProfessionalExperiences { get; set; } = new List<ProfessionnalExperience>();
    }
}
