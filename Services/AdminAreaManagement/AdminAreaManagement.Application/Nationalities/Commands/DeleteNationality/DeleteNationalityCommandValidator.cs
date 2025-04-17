using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Nationalities.Commands.DeleteNationality
{
    public class DeleteNationalityCommandValidator : AbstractValidator<DeleteNationalityCommand>
    {

        private readonly IRepositoryManager _repository;

        public DeleteNationalityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Id)
                .NotEmpty().WithMessage("NationalityId is required.");
        }
    }
}
