using FluentValidation;

namespace ClientManagement.Application.MonitoringActions.Commands.CreateAction
{
    public class CreateMonitoringActionCommandValidator : AbstractValidator<CreateMonitoringActionCommand>
    {
        public CreateMonitoringActionCommandValidator()
        {
            RuleFor(x => x.ActionLabel)
                .MinimumLength(1)
                .MaximumLength(30)
                .WithMessage("Action label must be between 1 and 30 characters");
        }
    }
}
