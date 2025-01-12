using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class Training : Entity
    {
        public string Name { get; set; }
        public int TrainingFieldId { get; set; }
        public TrainingField TrainingField { get; set; }
        public string TrainingFieldName => TrainingField.Name;
        public Training() { }
        public Training(string name, TrainingField trainingField)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Name = name;
            TrainingField = trainingField ?? throw new ArgumentNullException(nameof(trainingField)); ;
        }
    }
}