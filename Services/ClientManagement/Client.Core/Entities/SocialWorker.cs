using ClientManagement.Core.Common;
using ClientManagement.Core.ValueObjects;

namespace ClientManagement.Core.Entities
{
    public class SocialWorker : AggregateRoot
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName => $"{LastName} {FirstName}";
        public string UserName { get; set; }
        public string TeamName { get; set; }
        public string TeamAcronym { get; set; }
        public IList<Support> Supports { get; set; } = new List<Support>();
        public virtual IList<MonitoringReport> MonitoringReports { get; private set; } = new List<MonitoringReport>();
        public  SocialWorker()
        {
        }

        public SocialWorker(string firstName, string lastName,  string teamName,string teamAcronym, string userName) : this()
        {
            //Check if null
            if (string.IsNullOrEmpty(firstName))
            {
                throw new ArgumentNullException(nameof(firstName));
            }

            if (string.IsNullOrEmpty(teamName))
            {
                throw new ArgumentNullException(nameof(lastName));
            }

            if (string.IsNullOrEmpty(teamName))
            {
                throw new ArgumentNullException(nameof(teamName));
            }

            if (string.IsNullOrEmpty(teamAcronym))
            {
                throw new ArgumentNullException(nameof(teamAcronym));
            }

            if (string.IsNullOrEmpty(teamAcronym))
            {
                throw new ArgumentNullException(nameof(teamAcronym));
            }

            if (string.IsNullOrEmpty(userName))
            {
                throw new ArgumentNullException(nameof(userName));
            }

            FirstName = firstName ;
            LastName = lastName;
            UserName = userName;
            TeamName = teamAcronym;
            TeamName = teamAcronym;

        }

        public void AddSupport(Client client, DateTime startDate, string? note = "")
        {
            if (Supports.Any(x => x.ClientId.Equals(client.Id) && x.IsActif))
            {
                throw new InvalidOperationException("A support already exists for this client");
            }
            Supports.Add(new Support(client, startDate, this, note));
        }
    }
}
