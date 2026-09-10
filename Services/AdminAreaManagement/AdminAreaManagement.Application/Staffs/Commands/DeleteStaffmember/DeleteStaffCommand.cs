using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Staffs.Commands.DeleteStaff
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("TeamConfiguration.ManageStaffProfiles")]
    public class DeleteStaffMemberCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteStaffMemberCommandHandler : IRequestHandler<DeleteStaffMemberCommand>
        {
            private readonly IRepositoryManager _repository;
            private readonly IStaffProjectionOutbox _outbox;

            public DeleteStaffMemberCommandHandler(IRepositoryManager repository, IStaffProjectionOutbox outbox)
            {
                _repository = repository;
                _outbox = outbox;
            }

            public async Task Handle(DeleteStaffMemberCommand request, CancellationToken cancellationToken)
            {
                var foundedStaffMember = _repository.StaffMember.Get(request.Id);

                if (foundedStaffMember != null)
                {
                    if (foundedStaffMember.Softdelete)
                    {
                        foundedStaffMember.Softdelete = false;
                    }
                    else
                    {
                        foundedStaffMember.Softdelete = true;
                    }

                    var team = _repository.Team.Get(foundedStaffMember.TeamId)
                        ?? throw new NotFoundException(nameof(Team), foundedStaffMember.TeamId);
                    foundedStaffMember.AdvanceProjectionVersion();
                    _outbox.Stage(foundedStaffMember, team);
                    _repository.StaffMember.SoftDelete(foundedStaffMember);

                    _repository.Save();
                    await _outbox.DispatchAsync(cancellationToken);
                }
                else
                {
                    throw new NotFoundException(nameof(StaffMember), request.Id);
                }
            }
        }
    }
}
