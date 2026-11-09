using ClientManagement.Application.Clients.Queries.GetClients;

namespace ClientManagement.Application.Supports.Queries
{
    public class SupportDto
    {
        public int SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; } = DateTime.Now;
        public int StaffMemberId { get; set; }
        public int ClientId { get; set; }
        public string StaffMemberInfo { get; set; }
        public ClientLookUpDto Client { get; set; }
        public bool IsActif { get; set; }
        public string Note { get; set; }
        public string? ReasonOfClosure { get; set; }
        public bool HasNote { get; set; }
        public bool IsLastSupport { get; set; }
    }
}
