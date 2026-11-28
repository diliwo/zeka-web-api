using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Clients.Queries.GetBeneficiaryIdentity;

public sealed record BeneficiaryIdentityDto(int BeneficiaryId, string DisplayName)
{
    public int ContractVersion => 1;
}

[RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
public sealed record GetBeneficiaryIdentityQuery(int BeneficiaryId) : IRequest<BeneficiaryIdentityDto?>;

public sealed class GetBeneficiaryIdentityQueryHandler(IRepositoryManager repository)
    : IRequestHandler<GetBeneficiaryIdentityQuery, BeneficiaryIdentityDto?>
{
    public Task<BeneficiaryIdentityDto?> Handle(
        GetBeneficiaryIdentityQuery request, CancellationToken cancellationToken) =>
        repository.Client.GetClients()
            .Where(client => client.Id == request.BeneficiaryId && client.Id > 0)
            .Select(client => new BeneficiaryIdentityDto(client.Id, client.LastName + " " + client.FirstName))
            .SingleOrDefaultAsync(cancellationToken);
}
