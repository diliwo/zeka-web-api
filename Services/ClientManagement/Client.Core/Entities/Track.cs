using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class Track : Entity
    {
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int StaffMemberId { get; set; }
        public StaffMember StaffMember { get; set; }
        public int ClientId { get; set; }
        public Client Client { get; set; }
        public String? Note { get; set; }
        public string? ReasonOfClosure { get; set; }

        public bool IsActif => !EndDate.HasValue;

        public Track()
        {
        }

        public Track(Client client,  DateTime startDate, StaffMember StaffMember, String? note = "")
        {
            Client = client;
            StartDate = startDate;
            StaffMember = StaffMember;
            Note = note;
        }
    }
}
