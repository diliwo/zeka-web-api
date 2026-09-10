using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember.IntegrationEvents.Events;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using Zeka.Extensions.EventBus.Abstractions;

namespace AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("TeamConfiguration.ManageStaffProfiles")]
    public class UpdateStaffMemberCommand : IRequest<int>
    {
        public int StaffMemberId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int TeamId { get; set; }
        public string UserName { get; set; }

        public class UpsertStaffMemberCommandHandler : IRequestHandler<UpdateStaffMemberCommand, int>
        {
            private readonly IRepositoryManager _repository;
            private readonly IStaffProjectionOutbox _outbox;


            public UpsertStaffMemberCommandHandler(IRepositoryManager repository, IStaffProjectionOutbox outbox)
            {
                _repository = repository;
                _outbox = outbox;
            }

            public async Task<int> Handle(UpdateStaffMemberCommand request, CancellationToken cancellationToken)
            {
                StaffMember entity;

                entity = _repository.StaffMember.Get(request.StaffMemberId);
                if(entity == null)
                {
                    throw new NotFoundException(nameof(entity), request.StaffMemberId);
                }

                entity.FirstName = request.FirstName;
                entity.LastName = request.LastName;
                entity.TeamId = request.TeamId;
                if (!string.IsNullOrWhiteSpace(request.UserName))
                {
                    entity.UserName = request.UserName;
                }


                var team = _repository.Team.Get(request.TeamId)
                    ?? throw new NotFoundException(nameof(Team), request.TeamId);
                entity.AdvanceProjectionVersion();
                _outbox.Stage(entity, team);
                _repository.StaffMember.Persist(entity);
                _repository.Save();
                await _outbox.DispatchAsync(cancellationToken);


                return entity.Id;
            }
        }
    }
}
