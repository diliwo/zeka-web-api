using DiliBeneficiary.Core.Interfaces;
using FluentValidation;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.UpSertIbisNumber
{
    public class UpSertIbisNumberCommandValidator : AbstractValidator<UpSertIbisNumberCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpSertIbisNumberCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(command => command.PatchDoc).SetValidator(new JsonPatchDocumentValidator());
                //.OverridePropertyName("Property")
                //.WithMessage("Action impossible, nombre de caractères max autorisés : 20"); //must be 20 chars or fewer
        }

    }
}
