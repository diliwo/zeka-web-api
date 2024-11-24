using Client.Core.Interfaces;
using FluentValidation;

namespace Client.Application.Bilans.Commands.FinalizeBilan
{
    public class FinalizeBilanCommandValidator : AbstractValidator<FinalizeBilanCommand>
    {

        private readonly IRepositoryManager _repository;

        public FinalizeBilanCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BilanId)
                .MustAsync(async (obj, date, cancellationToken) =>
                {
                    bool result = await _repository.Bilan.IsBilanNotFinalizd((int)obj.BilanId);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action impossible, Le assessment est déjà finalisé !");
        }
    }
}
