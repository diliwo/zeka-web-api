using DiliBeneficiary.Core.Interfaces;
using FluentValidation;

namespace DiliBeneficiary.Application.BilanDocument.Commands.GenerateBilanDocumentCommand
{
    public class GenerateBilanDocumentCommandValidator : AbstractValidator<GenerateBilanDocumentCommand>
    {
        private readonly IRepositoryManager _repository;

        public GenerateBilanDocumentCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BilanId)
                .NotEmpty().WithMessage("BilanId is required.");
        }
    }
}
