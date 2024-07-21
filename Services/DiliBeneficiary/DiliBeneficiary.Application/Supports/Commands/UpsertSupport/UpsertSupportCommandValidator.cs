using DiliBeneficiary.Core.Interfaces;
using FluentValidation;

namespace DiliBeneficiary.Application.Supports.Commands.UpsertSupport
{
    public class UpsertSupportCommandValidator : AbstractValidator<UpsertSupportCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpsertSupportCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            When(suppot => suppot.SupportId == null, () =>
            {
                RuleFor(v => v.StartDate)
                    .MustAsync(async (obj, date, cancellationToken) =>
                    {
                        bool result = await _repository.Support.DateIsEarlierThanExistingDates(obj.BeneficiaryId, date);
                        return !result;
                    }).OverridePropertyName("Property").WithMessage("Action impossible, la date doit être postérieure au dernier suivi !");
            });

            When(suppot => suppot.SupportId != null, () =>
            {
                RuleFor(v => v.StartDate)
                    .MustAsync(async (obj, date, cancellationToken) =>
                    {
                        bool result =
                            await _repository.Support.DateIsEarlierThanExistingDates(obj.BeneficiaryId, date,
                               (int) obj.SupportId);
                        return !result;
                    }).OverridePropertyName("Property")
                    .WithMessage("Action impossible, la date doit être postérieure au dernier suivi !");
            });

            RuleFor(Support => Support.Note).MaximumLength(255)
                .OverridePropertyName("Property").WithMessage("Action impossible, nombre de caractères autorisés : 255"); //must be 250 chars or fewer
        }
    }
}
