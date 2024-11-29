using FluentValidation;

namespace ClientManagement.Application.QuarterlyMonitorings.Commands.UpdateQuarterlyMonitoring
{
    public class UpdateQuarterlyMonitoringCommandValidator : AbstractValidator<UpdateQuarterlyMonitoringCommand>
    {
        public UpdateQuarterlyMonitoringCommandValidator()
        {
            RuleFor(x => x.ActionDate)
                .NotEmpty()
                .WithMessage("Action date is required");
            //RuleFor(x => x.ActionComment)
            //    .MaximumLength(255)
            //    .WithMessage("Comment must be less than 255 characters");
        }
    }
}
