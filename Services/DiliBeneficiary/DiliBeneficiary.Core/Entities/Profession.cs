using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class Profession : Entity
    {
        public string Name { get; set; }

        public Profession(){}

        public Profession(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Name = name;
        }
    }
}
