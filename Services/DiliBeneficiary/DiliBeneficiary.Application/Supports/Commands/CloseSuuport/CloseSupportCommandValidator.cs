using DiliBeneficiary.Core.Interfaces;
using FluentValidation;

namespace DiliBeneficiary.Application.Supports.Commands.CloseSuuport
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

                }).OverridePropertyName("Property").WithMessage("Action impossible, La date de fin est incohérente !");
            RuleFor(support => support.ReasonOfClosure)
                .NotEmpty()
                .OverridePropertyName("Property")
                .WithMessage("La raison est obligatoire !");
        }
    }
}
