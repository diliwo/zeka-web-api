using ClientManagement.Core.Enums;

namespace ClientManagement.Core.Entities
{
    public class EmploymentStatusHistory
    {
        public EmploymentStatus EmploymentStatus { get; set; }
        public DateTime StartDate { get; set; }
        public Candidacy Candidacy { get; set; }
        public int CandidacyId { get; set; }
    }
}
