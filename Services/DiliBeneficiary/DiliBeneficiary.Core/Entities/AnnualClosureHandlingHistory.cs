namespace DiliBeneficiary.Core.Entities
{
    public class AnnualClosureHandlingHistory 
    {
        public DateTime ClosureStartDate { get; set; }
        public string Niss { get; set; }
        public string ReferentUserName { get; set; }
        public DateTime HandlingDate { get; set; } = DateTime.Now;
    }
}
