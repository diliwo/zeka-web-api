using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class Profession : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
    {
        public string Name { get; set; }

        public Profession(){}

        public Profession(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Name = name.Trim();
        }
    }
}
