using ClientManagement.Core.Enums;

namespace ClientManagement.Application.SchoolRegistations.Common
{
    public class SchoolRegistrationDto
    {
        public int SchoolRegistrationId { get; set; }
        public int TrainingId { get; set; }
        public string TrainingName { get; set; }
        public int SchoolId { get; set; }
        public string SchoolName { get; set; }
        public int TrainingTypeId { get; set; }
        public string TrainingTypeName { get; set; }
        public int ClientId { get; set; }
        public string BenficiaryName { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }
    }
}