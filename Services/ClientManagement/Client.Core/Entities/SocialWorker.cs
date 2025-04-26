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
        public IList<SocialCase> SocialCases { get; set; } = new List<SocialCase>();
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

            if (string.IsNullOrEmpty(lastName))
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

            if (string.IsNullOrEmpty(userName))
            {
                throw new ArgumentNullException(nameof(userName));
            }

            FirstName = firstName ;
            LastName = lastName;
            UserName = userName;
            TeamName = teamName;
            TeamAcronym = teamAcronym;

        }

        public void AddSocialCase(Client client, DateTime startDate, string? note = "")
        {
            if (SocialCases.Any(x => x.ClientId.Equals(client.Id) && x.IsActif))
            {
                throw new InvalidOperationException("A support already exists for this client");
            }
            SocialCases.Add(new SocialCase(client, startDate, this, note));
        }
    }
}
