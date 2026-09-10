using ClientManagement.Application.Clients.Model;

namespace ClientManagement.Application.Clients.Commands.UpSertIbisNumber;

// Delivery adapters own the wire patch format; Application validates its business values.
public interface IClientPatch
{
    IEnumerable<string> ReplacementValues { get; }
    void ApplyTo(UpdateClientDto client);
}
