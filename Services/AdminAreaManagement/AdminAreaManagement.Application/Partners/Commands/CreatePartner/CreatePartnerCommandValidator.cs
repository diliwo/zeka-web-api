using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Partners.Commands.CreatePartner
{
    public class CreatePartnerCommandValidator : AbstractValidator<CreatePartnerCommand>
    {
        private readonly IRepositoryManager _repository;

        public CreatePartnerCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(Partner => Partner.Note)
                .MaximumLength(255)
                .OverridePropertyName("Property")
                .WithMessage("Action not allowed, must be 255 chars or fewer");

            RuleFor(Partner => Partner.StreetName)
                .NotEmpty()
                .OverridePropertyName("Property")
                .NotNull()
                .WithMessage("The street name is mandatory !");

            RuleFor(Partner => Partner.StreetNumber)
                .NotEmpty().WithMessage("The street number is mandatory")
                .NotNull()
                .OverridePropertyName("Property");

            RuleFor(Partner => Partner.StreetCity)
                .NotEmpty().WithMessage("The name of the city is mandatory!")
                .NotNull()
                .OverridePropertyName("Property");

            RuleFor(Partner => Partner.StreetPostalCode)
                .NotEmpty().WithMessage("The postal code is mandatory !")
                .NotNull()
                .OverridePropertyName("Property");

            RuleFor(Partner => Partner.DateOfAgreementSignature)
                .NotEmpty().WithMessage("The date of signature is mandatory !")
                .NotNull()
                .OverridePropertyName("Property");

        }

    }
}
