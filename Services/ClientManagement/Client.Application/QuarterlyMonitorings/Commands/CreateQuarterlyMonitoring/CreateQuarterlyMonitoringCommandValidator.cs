using FluentValidation;

namespace Client.Application.QuarterlyMonitorings.Commands.CreateQuarterlyMonitoring
{
    public class CreateQuarterlyMonitoringCommandValidator : AbstractValidator<CreateQuarterlyMonitoringCommand>
    {
        public CreateQuarterlyMonitoringCommandValidator()
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
