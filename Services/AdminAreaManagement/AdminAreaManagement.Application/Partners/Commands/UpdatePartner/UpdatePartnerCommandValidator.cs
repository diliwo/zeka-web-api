using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Partners.Commands.UpdatePartner
{
    public class UpdatePartnerCommandValidator : AbstractValidator<UpdatePartnerCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpdatePartnerCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(Partner => Partner.Note)
                .MaximumLength(255)
                .OverridePropertyName("Property")
                .WithMessage("Action not allowed, must be 255 chars or fewer");

            RuleFor(Partner => Partner.StreetName)
                .NotEmpty()
                .WithMessage("The street name is mandatory !")
                .OverridePropertyName("Property")
                .NotNull();

            RuleFor(Partner => Partner.StreetNumber)
                .NotEmpty()
                .WithMessage("The street number is mandatory !")
                .OverridePropertyName("Property")
                .NotNull();

            RuleFor(Partner => Partner.StreetCity)
                .NotEmpty()
                .WithMessage("The city is mandatory!")
                .OverridePropertyName("Property")
                .NotNull();

            RuleFor(Partner => Partner.StreetPostalCode)
                .NotEmpty()
                .WithMessage("The postal code is mandatory !")
                .OverridePropertyName("Property")
                .NotNull();
        }

    }
}
