using ClientManagement.Core.Interfaces;
using FluentValidation;

namespace ClientManagement.Application.Supports.Commands.CloseTrack
{
    public class CloseSupportCommandValidator : AbstractValidator<CloseSupportCommand>
    {
        private readonly IRepositoryManager _repository;

        public CloseSupportCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.EndDate)
                .MustAsync(async (obj, date, cancellationToken) =>
                {
                    bool result = await _repository.Support.EndDateIsGreaterThanStartDate(obj.SupportId, date);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, The end date is not consistent !");
            RuleFor(support => support.ReasonOfClosure)
                .NotEmpty()
                .OverridePropertyName("Property")
                .WithMessage("The reason is mandatory !");
        }
    }
}
