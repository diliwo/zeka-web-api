using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember.IntegrationEvents.Events;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;
using AdminAreaManagement.Application.Common.Authorization;

namespace AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("TeamConfiguration.ManageStaffProfiles")]
    public class CreateStaffMemberCommand : IRequest<int>
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int TeamId { get; set; }
        public string UserName { get; set; }
        public Guid OrganisationMembershipId { get; set; }

        public class CreateStaffMemberCommandHandler : IRequestHandler<CreateStaffMemberCommand, int>
        {
            private readonly IRepositoryManager _repository;
            private readonly IStaffProjectionOutbox _outbox;
            private readonly IStaffMembershipLink _membership;
            private readonly ITenantContextAccessor _tenant;

            public CreateStaffMemberCommandHandler(IRepositoryManager repository, IStaffProjectionOutbox outbox,
                IStaffMembershipLink membership, ITenantContextAccessor tenant)
            {
                _repository = repository;
                _outbox = outbox;
                _membership = membership;
                _tenant = tenant;
            }

            public async Task<int> Handle(CreateStaffMemberCommand request, CancellationToken cancellationToken)
            {
                StaffMember entity;
                var foundedService = _repository.Team.Get(request.TeamId);
                if (foundedService == null)
                {
                    throw new NotFoundException(nameof(foundedService), request.TeamId);
                }

                entity = new StaffMember(request.FirstName, request.LastName, foundedService, request.UserName);
                if (!await _membership.IsActiveAsync(_tenant.Current.OrganisationId.Value,
                    request.OrganisationMembershipId, cancellationToken))
                    throw new TenantAccessException(AccessFailure.Denied);
                entity.LinkMembership(request.OrganisationMembershipId);
                _outbox.Stage(entity, foundedService);

                _repository.StaffMember.Persist(entity);
                _repository.Save();

                //get team
                var team = _repository.Team.Get(entity.TeamId);
                if (team == null)
                {
                    throw new NotFoundException(nameof(team), request.TeamId);
                }

                //We send an event to the message broker
                await _outbox.DispatchAsync(cancellationToken);

                return entity.Id;
            }
        }
    }
}
