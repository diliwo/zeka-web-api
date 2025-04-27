using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember.IntegrationEvents.Events;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using Zeka.Extensions.EventBus.Abstractions;
using Microsoft.AspNetCore.Http;
using Tenant;

namespace AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember
{
    public class CreateStaffMemberCommand : IRequest<int>
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int TeamId { get; set; }
        public string UserName { get; set; }

        public class CreateStaffMemberCommandHandler : IRequestHandler<CreateStaffMemberCommand, int>
        {
            private readonly IRepositoryManager _repository;
            private readonly IEventBus _eventBus;
            private readonly ITenantService _tenantService;

            public string TenantName { get => _tenantService.GetTenant()?.TenantName ?? String.Empty; }

            public CreateStaffMemberCommandHandler(IRepositoryManager repository, IEventBus eventBus, ITenantService tenantService)
            {
                _repository = repository;
                _eventBus = eventBus;
                _tenantService = tenantService;
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

                _repository.StaffMember.Persist(entity);
                _repository.Save();

                //get team
                var team = _repository.Team.Get(entity.TeamId);
                if (team == null)
                {
                    throw new NotFoundException(nameof(team), request.TeamId);
                }

                //We send an event to the message broker
                _eventBus.PublishAsync(new SocialWorkerCreatedEvent(entity.FirstName, entity.LastName, entity.UserName, team.Name, team.Acronym, TenantName));

                return entity.Id;
            }
        }
    }
}