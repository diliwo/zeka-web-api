using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class StaffMember : AggregateRoot
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName => $"{LastName} {FirstName}";
        public string UserName { get; set; }
        public string TeamName { get; set; }

        // Just for hydratation
        public  StaffMember()
        {
        }

        public StaffMember(string firstName, string lastName,  string teamName, string userName = "") : this()
        {
            //Check if null
            if (string.IsNullOrEmpty(firstName))
            {
                throw new ArgumentNullException(nameof(firstName));
            }

            if (string.IsNullOrEmpty(lastName))
            {
                throw new ArgumentNullException(nameof(lastName));
            }

            FirstName = firstName ;
            LastName = lastName;
            UserName = userName ;
            Team = team ?? throw new ArgumentNullException(nameof(team));
        }
    }
}
