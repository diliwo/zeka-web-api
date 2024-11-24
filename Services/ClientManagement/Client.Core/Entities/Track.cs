using Client.Core.Common;

namespace Client.Core.Entities
{
    public class Track : Entity
    {
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int StaffId { get; set; }
        public Staff Staff { get; set; }
        public int ClientId { get; set; }
        public Client Client { get; set; }
        public String? Note { get; set; }
        public string? ReasonOfClosure { get; set; }

        public bool IsActif => !EndDate.HasValue;

        public Track()
        {
        }

        public Track(Client client,  DateTime startDate, Staff staff, String? note = "")
        {
            Client = client;
            StartDate = startDate;
            Staff = staff;
            Note = note;
        }
    }
}
