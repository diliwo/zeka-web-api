using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember.IntegrationEvents.Events;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using Zeka.Extensions.EventBus.Abstractions;

namespace AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember
{
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
            private readonly IEventBus _eventBus;


            public UpsertStaffMemberCommandHandler(IRepositoryManager repository, IEventBus eventBus)
            {
                _repository = repository;
                _eventBus = eventBus;
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
                

                _repository.StaffMember.Persist(entity);
                _repository.Save();


                return entity.Id;
            }
        }
    }
}