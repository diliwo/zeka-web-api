using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class TrainingType : Entity
    {
        public string Name { get; set; }

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
