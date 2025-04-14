using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Nationalities.Commands.UpdateCity
{
    public class UpdateNationalityCommandValidator : AbstractValidator<UpdateNationalityCommand>
    {

        private readonly IRepositoryManager _repository;

        public UpdateNationalityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.NationalityId)
                .NotEmpty().WithMessage("NationalityId is required.");
        }
    }
}
