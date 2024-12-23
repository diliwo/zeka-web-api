using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.MonitoringActions.Commands.DeleteAction
{
    public class DeleteMonitoringActionCommand : IRequest
    {
        public int ActionId { get; set; }

        public DeleteMonitoringActionCommand(int actionId)
        {
            ActionId = actionId;
        }
    }

    public class DeleteMonitoringActionCommandHandler : IRequestHandler<DeleteMonitoringActionCommand>
    {
        private IMonitoringActionRepository _monitoringActionRepository;

        public DeleteMonitoringActionCommandHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public async Task Handle(DeleteMonitoringActionCommand request, CancellationToken cancellationToken)
        {
            var action = await _monitoringActionRepository.GetMonitoringActionById(request.ActionId).SingleOrDefaultAsync(cancellationToken);
            if (action == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.ActionId);
            }
            _monitoringActionRepository.SoftDelete(request.ActionId);
        }
    }
}
