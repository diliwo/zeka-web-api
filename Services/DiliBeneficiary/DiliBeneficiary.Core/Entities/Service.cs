using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Exceptions;

namespace DiliBeneficiary.Core.Entities
{
    public class Service : Entity
    {
        public string Name { get; set; }
        public string Acronym { get; set; }

        public Service() {}

        public Service(string name, string acronym)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrEmpty(acronym))
            {
                throw new ArgumentNullException(nameof(acronym));
            }

            if (acronym.Length > 7 && acronym.Length < 2)
            {
                throw new InvalidAcronymFormatException(acronym);
            }

            Name = name;
            Acronym = acronym.ToUpper();
        }
        public ICollection<Referent> Referents { get; private set; } = new HashSet<Referent>();
    }
}
