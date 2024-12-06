using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Teams.Commands.UpsertTeam
{
    public class UpsertTeamCommandValidator : AbstractValidator<UpsertTeamCommand>
    {

        private readonly IRepositoryManager _repository;

        public UpsertTeamCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Title is required.")
                .MaximumLength(200).WithMessage("Title must not exceed 200 characters.");
        }
    }
}
