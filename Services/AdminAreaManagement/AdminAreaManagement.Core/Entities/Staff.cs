using AdminAreaManagement.Core.Commun;

namespace AdminAreaManagement.Core.Entities
{
    public class StaffMember : AggregateRoot
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName => $"{LastName} {FirstName}";
        public string UserName { get; set; }
        public int TeamId { get; set; }
        public Team? Team { get; set; }

        // Just for hydratation
        public  StaffMember()
        {
        }

        public StaffMember(string firstName, string lastName,  Team team, string userName = "") : this()
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
