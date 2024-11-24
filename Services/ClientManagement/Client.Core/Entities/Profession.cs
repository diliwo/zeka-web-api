using Client.Core.Common;

namespace Client.Core.Entities
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
