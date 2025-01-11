using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.QuarterlyMonitorings.Commands.UpdateQuarterlyMonitoring
{
    public class UpdateQuarterlyMonitoringCommand : IRequest<int>
    {
        public int QMonitoringId { get; set; }
        public int ClientId { get; set; }
        public int StaffMemberId { get; set; }
        public int MonitoringActionId { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }

    }

    public class UpdateQuarterlyMonitoringCommandHandler : IRequestHandler<UpdateQuarterlyMonitoringCommand, int>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        private readonly IRepositoryManager _repository;

        public UpdateQuarterlyMonitoringCommandHandler(
            IRepositoryManager repository,
            IMonitoringActionRepository monitoringActionRepository)
        {
            _repository = repository;
            _monitoringActionRepository = monitoringActionRepository;
        }


        public async Task<int> Handle(UpdateQuarterlyMonitoringCommand request, CancellationToken cancellationToken)
        {
            var qMonitoring = await _repository.QuarterlyMonitoring.GetQuarterlyMonitoringById(request.QMonitoringId)
                .SingleOrDefaultAsync(cancellationToken);
            if (qMonitoring == null)
            {
                throw new NotFoundException(nameof(MonitoringReport), request.QMonitoringId);
            }

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

            var monitoringAction = await _monitoringActionRepository.GetMonitoringActionById(request.MonitoringActionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (monitoringAction == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.MonitoringActionId);
            }

            qMonitoring.Client = Client;
            qMonitoring.ClientId = request.ClientId;
            qMonitoring.SocialWorker = StaffMember;
            qMonitoring.SocialWorkerId = request.StaffMemberId;
            qMonitoring.MonitoringAction = monitoringAction;
            qMonitoring.MonitoringActionId = request.MonitoringActionId;
            qMonitoring.ActionDate = request.ActionDate.ToLocalTime();
            qMonitoring.ActionComment = request.ActionComment;
            return await _repository.QuarterlyMonitoring.Persist(qMonitoring);
        }
    }
}
