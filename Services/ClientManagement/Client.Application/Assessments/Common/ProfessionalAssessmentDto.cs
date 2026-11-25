namespace ClientManagement.Application.Assessments.Common
{
    public class ProfessionalAssessmentDto
    {
        public int? BilanProfessionId { get; set; }
        public int BilanId { get; set; }
        public string ProfessionTitle { get; set; }
        public int? ProfessionId { get; set; }
        public int? BenficiaryId { get; set; }
        public string AcquiredKnowledge { get; set; }
        public string AcquiredBehaviouralKnowledge { get; set; }
        public string AcquiredKnowHow { get; set; }
        public string KnowledgeToDevelop { get; set; }
        public string BehaviouralKnowledgeToDevelop { get; set; }
        public string KnowHowToDevelop { get; set; }
    }
}
