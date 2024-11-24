using Client.Core.Interfaces;
using FluentValidation;

namespace Client.Application.Bilans.Commands.UpdateBilan
{
    public class UpdateBilanCommandValidator : AbstractValidator<UpdateBilanCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpdateBilanCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BilanId)
                .NotEmpty().WithMessage("Id is required.");
        }
    }
}
