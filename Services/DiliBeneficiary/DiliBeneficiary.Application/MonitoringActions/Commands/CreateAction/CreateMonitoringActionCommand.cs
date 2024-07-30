using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.MonitoringActions.Commands.CreateAction
{
    public class CreateMonitoringActionCommand : IRequest
    {
        public string ActionLabel { get; set; }
        public CreateMonitoringActionCommand(string actionLabel)
        {
            ActionLabel = actionLabel;
        }
    }

    public class CreateMonitoringActionCommandHandler : IRequestHandler<CreateMonitoringActionCommand>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        public CreateMonitoringActionCommandHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public Task Handle(CreateMonitoringActionCommand request, CancellationToken cancellationToken)
        {
            _monitoringActionRepository.Persist(new MonitoringAction(request.ActionLabel));
            return Unit.Task;
        }
    }
}
