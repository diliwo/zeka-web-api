using Client.Core.Interfaces;
using FluentValidation;

namespace Client.Application.Bilans.Commands.CreateBilan
{
    public class CreateBilanCommandValidator : AbstractValidator<CreateBilanCommand>
    {
        private readonly IRepositoryManager _repository;

        public CreateBilanCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.ClientId)
                .NotEmpty().WithMessage("ClientId is required.")
                .MustAsync(async (obj, id,cancellationToken) =>
                {
                    bool result = await _repository.Bilan.AreAllBilansNotFinalized(obj.ClientId);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action impossible, Il existe un assessment non finalisé ");
        }
    }
}
