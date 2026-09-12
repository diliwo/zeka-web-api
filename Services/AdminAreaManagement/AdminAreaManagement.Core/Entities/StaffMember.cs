using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class StaffMember : AggregateRoot
    {
        public Guid OrganisationMembershipId { get; private set; }
        public long ProjectionVersion { get; private set; } = 1;

        public void LinkMembership(Guid membershipId)
        {
            if (membershipId == Guid.Empty || (OrganisationMembershipId != Guid.Empty && OrganisationMembershipId != membershipId))
                throw new InvalidOperationException("Staff membership linkage must be non-empty and immutable.");
            OrganisationMembershipId = membershipId;
        }

        public void AdvanceProjectionVersion() => ProjectionVersion = checked(ProjectionVersion + 1);
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
