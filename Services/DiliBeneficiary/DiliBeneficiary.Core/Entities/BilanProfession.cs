using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class BilanProfession : Entity
    {
        public int BilanId { get; set; }
        public virtual Bilan Bilan { get; set; }
        public int? ProfessionId { get; set; }
        public virtual Profession? Profession { get; set; }
        public string AcquiredKnowledge { get; set; }
        public string AcquiredBehaviouralKnowledge { get; set; }
        public string AcquiredKnowHow { get; set; }
        public string KnowledgeToDevelop { get; set; }
        public string BehaviouralKnowledgeToDevelop { get; set; }
        public string KnowHowToDevelop { get; set; }

        public BilanProfession(){}

        public BilanProfession(
            Bilan bilan,
            Profession profession,
            string acquiredKnowledge,
            string acquiredBehaviouralKnowledge,
            string acquiredKnowHow,
            string knowledgeToDevelop,
            string behaviouralKnowledgeToDevelop,
            string knowHowToDevelop
            )
        {
            Bilan = bilan ?? throw new ArgumentNullException(nameof(bilan));
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
