using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Services.Commands.UpsertService
{
    public class UpsertServiceCommandValidator : AbstractValidator<UpsertTeamCommand>
    {

        private readonly IRepositoryManager _repository;

        public UpsertServiceCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Title is required.")
                .MaximumLength(200).WithMessage("Title must not exceed 200 characters.");
        }
    }
}
