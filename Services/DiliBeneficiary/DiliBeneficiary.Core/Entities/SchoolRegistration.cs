using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Core.Entities
{
    public class SchoolRegistration : Entity
    {
        public int FormationId { get; set; }
        public virtual Formation Formation { get; set; }
        public int SchoolId { get; set; }
        public virtual School School { get; set; }
        public int TrainingTypeId { get; set; }
        public virtual TrainingType TrainingType { get; set; }
        public int BeneficiaryId { get; set; }
        public virtual Beneficiary Beneficiary { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; } = Level.Other;
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        public SchoolRegistration() { }

        public SchoolRegistration(Formation formation,
            School school,
            TrainingType trainingType,
            Beneficiary beneficiary,
            Level level,
            SchoolResult result,
            DateTime start,
            DateTime end,
            string? note = "")
        {
            Formation = formation ?? throw new ArgumentNullException(nameof(formation));
            School = school ?? throw new ArgumentNullException(nameof(school));
            TrainingType = trainingType ?? throw new ArgumentNullException(nameof(trainingType));
            Beneficiary = beneficiary ?? throw new ArgumentNullException(nameof(beneficiary));
            StartDate = start;
            EnDate = end;
            Result = result;
            CourseLevel = level;
            Note = note;
        }
    }
}
