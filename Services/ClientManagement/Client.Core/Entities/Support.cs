using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class Support : Entity
    {
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int SocialWorkerId { get; set; }
        public SocialWorker SocialWorker { get; set; }
        public int ClientId { get; set; }
        public Client Client { get; set; }
        public String? Note { get; set; }
        public string? ReasonOfClosure { get; set; }

        public bool IsActif => !EndDate.HasValue;

        public Support()
        {
        }

        public Support(Client client,  DateTime startDate, SocialWorker socialWorker, String? note = "")
        {
            Client = client;
            StartDate = startDate;
            SocialWorker = socialWorker;
            Note = note;
        }
    }
}
