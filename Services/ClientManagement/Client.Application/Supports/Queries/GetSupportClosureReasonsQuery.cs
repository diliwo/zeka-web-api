using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using MediatR;

namespace ClientManagement.Application.Supports.Queries;

[RequiresTenantPermission("ReferenceData.View")]
public sealed record GetSupportClosureReasonsQuery : IRequest<IEnumerable<ReasonOfClosure>>
{
    public sealed class Handler(IGenericReadRepository<ReasonOfClosure> repository)
        : IRequestHandler<GetSupportClosureReasonsQuery, IEnumerable<ReasonOfClosure>>
    {
        public async Task<IEnumerable<ReasonOfClosure>> Handle(GetSupportClosureReasonsQuery request, CancellationToken cancellationToken)
            => await repository.GetItemsAsync();
    }
}
