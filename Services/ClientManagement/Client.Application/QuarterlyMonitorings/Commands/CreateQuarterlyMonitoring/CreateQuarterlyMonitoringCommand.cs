using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.QuarterlyMonitorings.Commands.CreateQuarterlyMonitoring
{
    public class CreateQuarterlyMonitoringCommand : IRequest<int>
    {
        public int ClientId { get; set; }
        public int StaffId { get; set; }
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
            var Staff = _repository.Staff.Get(request.StaffId);
            if (Staff == null)
            {
                throw new NotFoundException(nameof(Staff), request.StaffId);
            }
            var monitoringAction = await _monitoringActionRepository.GetMonitoringActionById(request.MonitoringActionId).SingleOrDefaultAsync(cancellationToken);
            if (monitoringAction == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.MonitoringActionId);
            }
            return await _repository.QuarterlyMonitoring.Persist(new QuarterlyMonitoring(request.ClientId, request.StaffId, request.MonitoringActionId, request.ActionDate.ToLocalTime(), request.ActionComment));
        }
    }
}
