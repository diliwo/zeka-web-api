using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class TrainingType : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
    {
        public string Name { get; set; }

        public TrainingType() { }
        public TrainingType(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Name = name;
        }
    }
}
