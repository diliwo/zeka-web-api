using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Staffs.Commands.UpsertStaff.IntegrationEvents.Events;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using Zeka.Extensions.EventBus.Abstractions;

namespace AdminAreaManagement.Application.Staffs.Commands.UpsertStaff
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
            private readonly IEventBus _eventBus;


            public UpsertStaffMemberCommandHandler(IRepositoryManager repository, IEventBus eventBus)
            {
                _repository = repository;
                _eventBus = eventBus;
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

                //We send an event to the message broker
                _eventBus.PublishAsync(new StaffMemberUpsertEvent($"{entity.FirstName} {entity.LastName}"));

                return entity.Id;
            }
        }
    }
}