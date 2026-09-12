using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class School : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
    {
        public string Name { get; set; }
        public string Locality { get; set; }

        public School() { }
        public School(string name, string locality)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name), $"{nameof(name)} cannot be null or empty.");
            }

            if (string.IsNullOrEmpty(locality))
            {
                throw new ArgumentNullException(nameof(locality), $"{nameof(locality)} cannot be null or empty.");
            }

            Name = name;
            Locality = locality;
        }
    }
}
