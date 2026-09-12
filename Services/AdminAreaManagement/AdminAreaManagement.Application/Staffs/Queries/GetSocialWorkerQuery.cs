using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Staffs.Queries;

public sealed record SocialWorkerResult(int Id, string FirstName, string LastName, string UserName, string TeamName, string TeamAcronym);

[RequiresTenantPermission("TeamConfiguration.View")]
public sealed class GetSocialWorkerQuery : IRequest<SocialWorkerResult>
{
    public int SocialWorkerId { get; set; }
    public sealed class GetSocialWorkerQueryHandler(IRepositoryManager repository) : IRequestHandler<GetSocialWorkerQuery, SocialWorkerResult>
    {
        public Task<SocialWorkerResult> Handle(GetSocialWorkerQuery request, CancellationToken cancellationToken)
        {
            var staff = repository.StaffMember.Get(request.SocialWorkerId)
                ?? throw new NotFoundException("Staff member");
            var team = repository.Team.Get(staff.TeamId) ?? throw new NotFoundException("Team");
            return Task.FromResult(new SocialWorkerResult(staff.Id, staff.FirstName, staff.LastName, staff.UserName, team.Name, team.Acronym));
        }
    }
}
