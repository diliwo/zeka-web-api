using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Exceptions;

namespace AdminAreaManagement.Core.Entities
{
    public class Team : Entity
    {
        public string Name { get; set; }
        public string Acronym { get; set; }

        public Team() {}

        public Team(string name, string acronym)
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
        public ICollection<StaffMember> StaffMembers { get; private set; } = new HashSet<StaffMember>();
    }
}
