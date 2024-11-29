using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Referents.Commands.UpsertReferent
{
    public class UpsertStaffMemberCommand : IRequest<int>
    {
        public int? StaffMemberId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int TeamId { get; set; }
        public string UserName { get; set; }

        public class UpsertStaffMemberCommandHandler : IRequestHandler<UpsertStaffMemberCommand, int>
        {
            private readonly IRepositoryManager _repository;


            public UpsertStaffMemberCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpsertStaffMemberCommand request, CancellationToken cancellationToken)
            {
                StaffMember entity;

                if (request.StaffMemberId.HasValue)
                {
                    entity = _repository.StaffMember.Get(request.StaffMemberId.Value);
                    entity.FirstName = request.FirstName;
                    entity.LastName = request.LastName;
                    entity.TeamId = request.TeamId;
                    if (!string.IsNullOrWhiteSpace(request.UserName))
                    {
                        entity.UserName = request.UserName;
                    }
                }
                else
                {
                    var foundedService = _repository.Team.Get(request.TeamId);
                    if (foundedService == null)
                    {
                        throw new NotFoundException(nameof(foundedService), request.TeamId);
                    }

                    entity = new StaffMember(request.FirstName, request.LastName, foundedService, request.UserName);
                }

                _repository.StaffMember.Persist(entity);

                _repository.Save();

                return entity.Id;
            }
        }
    }
}