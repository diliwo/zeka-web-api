using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;

namespace ClientManagement.Application.Clients.Queries.GetClients;

internal static class ClientLookupProjection
{
    internal static IQueryable<ClientLookUpDto> ProjectLookup(this IQueryable<Client> clients) =>
        clients.Select(client => new ClientLookUpDto
        {
            ClientId = client.Id,
            EmailAddress = client.Email!.EmailAddress,
            Name = client.LastName + " " + client.FirstName,
            Ssn = client.Ssn,
            Gender = client.Gender == Gender.Male ? "H" : "F"
        });
}
