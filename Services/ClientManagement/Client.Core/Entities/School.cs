using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class School : Entity
    {
        public string Name { get; set; }
        public string Locality { get; set; }

        public School(string name, string locality)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrEmpty(locality))
            {
                throw new ArgumentNullException(nameof(locality));
            }

            Name = name;
            Locality = locality;
        }
    }
}
