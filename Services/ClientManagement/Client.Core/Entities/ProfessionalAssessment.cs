using Client.Core.Common;

namespace Client.Core.Entities
{
    public class ProfessionalAssessment : Entity
    {
        public int AssessmentId { get; set; }
        public virtual Assessment Assessment { get; set; }
        public int? ProfessionId { get; set; }
        public virtual Profession? Profession { get; set; }
        public string AcquiredKnowledge { get; set; }
        public string AcquiredBehaviouralKnowledge { get; set; }
        public string AcquiredKnowHow { get; set; }
        public string KnowledgeToDevelop { get; set; }
        public string BehaviouralKnowledgeToDevelop { get; set; }
        public string KnowHowToDevelop { get; set; }

        public ProfessionalAssessment(){}

        public ProfessionalAssessment(
            Assessment assessment,
            Profession profession,
            string acquiredKnowledge,
            string acquiredBehaviouralKnowledge,
            string acquiredKnowHow,
            string knowledgeToDevelop,
            string behaviouralKnowledgeToDevelop,
            string knowHowToDevelop
            )
        {
            Assessment = assessment ?? throw new ArgumentNullException(nameof(assessment));
            Profession = profession;
            AcquiredKnowledge = acquiredKnowledge;
            AcquiredBehaviouralKnowledge = acquiredBehaviouralKnowledge;
            AcquiredKnowHow = acquiredKnowHow;
            KnowledgeToDevelop = knowledgeToDevelop;
            BehaviouralKnowledgeToDevelop = behaviouralKnowledgeToDevelop;
            KnowHowToDevelop = knowHowToDevelop;
        }
    }
}
