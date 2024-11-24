using Client.Core.Common;
using Client.Core.Enums;

namespace Client.Core.Entities
{
    public class SchoolEnrollment : Entity
    {
        public int TrainingId { get; set; }
        public virtual Training Training { get; set; }
        public int SchoolId { get; set; }
        public virtual School School { get; set; }
        public int TrainingTypeId { get; set; }
        public virtual TrainingType TrainingType { get; set; }
        public int ClientId { get; set; }
        public virtual Client Client { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; } = Level.Other;
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        public SchoolEnrollment() { }

        public SchoolEnrollment(Training training,
            School school,
            TrainingType trainingType,
            Client client,
            Level level,
            SchoolResult result,
            DateTime start,
            DateTime end,
            string? note = "")
        {
            Training = training ?? throw new ArgumentNullException(nameof(training));
            School = school ?? throw new ArgumentNullException(nameof(school));
            TrainingType = trainingType ?? throw new ArgumentNullException(nameof(trainingType));
            Client = client ?? throw new ArgumentNullException(nameof(client));
            StartDate = start;
            EnDate = end;
            Result = result;
            CourseLevel = level;
            Note = note;
        }
    }
}
