using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.MonitoringActions.Commands.UpdateAction
{
    public class UpdateMonitoringActionCommand : IRequest
    {
        public int ActionId { get; set; }
        public string ActionLabel { get; set; }
        public UpdateMonitoringActionCommand(int actionId, string actionLabel)
        {
            ActionId = actionId;
            ActionLabel = actionLabel;
        }
    }

    public class UpdateMonitoringActionCommandHandler : IRequestHandler<UpdateMonitoringActionCommand>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        public UpdateMonitoringActionCommandHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public async Task Handle(UpdateMonitoringActionCommand request, CancellationToken cancellationToken)
        {
            var action = await _monitoringActionRepository.GetMonitoringActionById(request.ActionId).SingleOrDefaultAsync(cancellationToken);
            if (action == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.ActionId);
            }
            action.Action = request.ActionLabel;
            _monitoringActionRepository.Persist(action);
        }
    }
}
