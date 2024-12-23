using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.QuarterlyMonitorings.Commands.CreateQuarterlyMonitoring
{
    public class CreateQuarterlyMonitoringCommand : IRequest<int>
    {
        public int ClientId { get; set; }
        public int StaffMemberId { get; set; }
        public int MonitoringActionId { get; set; }
        public DateTime ActionDate { get; set; } = DateTime.Now;
        public string ActionComment { get; set; } = "";
    }

    public class CreateQuarterlyMonitoringCommandHandler : IRequestHandler<CreateQuarterlyMonitoringCommand, int>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        public CreateQuarterlyMonitoringCommandHandler(IRepositoryManager repository, IMonitoringActionRepository monitoringActionRepository)
        {
            _repository = repository;
            _monitoringActionRepository = monitoringActionRepository;
        }
        

        public async Task<int> Handle(CreateQuarterlyMonitoringCommand request, CancellationToken cancellationToken)
        {
            var Client = _repository.Client.Get(request.ClientId);
            if (Client == null)
            {
                throw new NotFoundException(nameof(Client), request.ClientId);
            }
            var StaffMember = _repository.StaffMember.Get(request.StaffMemberId);
            if (StaffMember == null)
            {
                throw new NotFoundException(nameof(StaffMember), request.StaffMemberId);
            }
            var monitoringAction = await _monitoringActionRepository.GetMonitoringActionById(request.MonitoringActionId).SingleOrDefaultAsync(cancellationToken);
            if (monitoringAction == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.MonitoringActionId);
            }
            return await _repository.QuarterlyMonitoring.Persist(new QuarterlyMonitoring(request.ClientId, request.StaffMemberId, request.MonitoringActionId, request.ActionDate.ToLocalTime(), request.ActionComment));
        }
    }
}
