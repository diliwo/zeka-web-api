using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class Formation : Entity
    {
        public string Name { get; set; }
        public int TrainingFieldId { get; set; }
        public TrainingField TrainingField { get; set; }
        public string TrainingFieldName => TrainingField.Name;
        public Formation() { }
        public Formation(string name, TrainingField trainingField)
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