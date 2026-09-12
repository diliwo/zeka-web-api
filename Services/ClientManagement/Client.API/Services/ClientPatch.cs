using ClientManagement.Application.Clients.Commands.UpSertIbisNumber;
using ClientManagement.Application.Clients.Model;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.JsonPatch.Operations;

namespace ClientManagement.API.Services;

public sealed class ClientPatch(JsonPatchDocument<UpdateClientDto> document) : IClientPatch
{
    public IEnumerable<string> ReplacementValues => document.Operations
        .Where(operation => operation.OperationType is OperationType.Add or OperationType.Replace)
        .Select(operation => operation.value).OfType<string>();
    public void ApplyTo(UpdateClientDto client) => document.ApplyTo(client);
}
