namespace ClientManagement.Application.Clients.Queries.GetClients
{
    public class ClientLookUpDto
    {
        public int ClientId { get; set; }
        public string EmailAddress { get; set; }
        public string Name { get; set; }
        public string Ssn { get; set; }
        public string Gender { get; set; }
    }
}
