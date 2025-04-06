using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class School : Entity
    {
        public string Name { get; set; }
        public string Locality { get; set; }

        public School(string name, string locality)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException("",$"{nameof(name)} cannot be null or empty.");
            }

            if (string.IsNullOrEmpty(locality))
            {
                throw new ArgumentNullException("",$"{nameof(locality)} cannot be null or empty.");
            }

            Name = name;
            Locality = locality;
        }
    }
}
