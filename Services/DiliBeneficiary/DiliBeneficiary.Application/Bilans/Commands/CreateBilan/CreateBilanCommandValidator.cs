using DiliBeneficiary.Core.Interfaces;
using FluentValidation;

namespace DiliBeneficiary.Application.Bilans.Commands.CreateBilan
{
    public class CreateBilanCommandValidator : AbstractValidator<CreateBilanCommand>
    {
        private readonly IRepositoryManager _repository;

        public CreateBilanCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BeneficiaryId)
                .NotEmpty().WithMessage("BeneficiaryId is required.")
                .MustAsync(async (obj, id,cancellationToken) =>
                {
                    bool result = await _repository.Bilan.AreAllBilansNotFinalized(obj.BeneficiaryId);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action impossible, Il existe un bilan non finalisé ");
        }
    }
}
