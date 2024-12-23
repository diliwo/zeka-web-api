namespace ClientManagement.Core.Common.Dto
{
    public class EngagementWithAnnualClosure
    {
        public string LastName { get; set; }
        public string FirstName { get; set; }
        public string Niss { get; set; }
        public DateTime OccupationStartDate { get; set; }
        public DateTime? OccupationEndDate { get; set; }
        public string Partner { get; set; }
        public string JobCoachUserName { get; set; }
        public DateTime AnnualClosureStartDate { get; set; }
        public DateTime AnnualClosureEndDate { get; set; }


    }
}
