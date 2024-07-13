using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class Referent : AggregateRoot
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string FullName => $"{LastName} {FirstName}";
        public string UserName { get; set; }
        public int ServiceId { get; set; }
        public Service? Service { get; set; }
        public IList<Support> Supports { get; set; } = new List<Support>();
        public virtual IList<QuarterlyMonitoring> QuarterlyMonitorings { get; private set; } = new List<QuarterlyMonitoring>();

        // Just for hydratation
        public  Referent()
        {
        }

        public Referent(string firstName, string lastName,  Service service, string userName = "") : this()
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
            Service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public void AddSupport(Beneficiary beneficiary, DateTime startDate, string? note = "")
        {
            //TODO : Checker si existe déjà 
            if (Supports.Any(x => x.BeneficiaryId == beneficiary.Id && x.IsActif))
            {
                throw new InvalidOperationException("Un accompagnement existe déjà pour ce beneficaire !");
            }
            Supports.Add(new Support(beneficiary, startDate, this, note));
        }

        public void CloseSupport(int supportId)
        {
        }
    }
}
