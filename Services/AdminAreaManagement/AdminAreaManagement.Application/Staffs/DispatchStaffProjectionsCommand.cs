using AdminAreaManagement.Application.Common.Authorization;
using MediatR;

namespace AdminAreaManagement.Application.Staffs;

// A job host must create a fresh operation scope with its authenticated identity and selected organisation.
[RequiresTenantPermission("TeamConfiguration.ManageStaffProfiles")]
public sealed record DispatchStaffProjectionsCommand : IRequest
{
    public sealed class Handler(IStaffProjectionOutbox outbox) : IRequestHandler<DispatchStaffProjectionsCommand>
    {
        public Task Handle(DispatchStaffProjectionsCommand request, CancellationToken cancellationToken)
            => outbox.DispatchAsync(cancellationToken);
    }
}
