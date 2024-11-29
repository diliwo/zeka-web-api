using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Professions.Commands.UpsertProfession
{
    public class UpsertProfessionCommandValidator : AbstractValidator<UpsertProfessionCommand>
    {

        private readonly IRepositoryManager _repository;

        public UpsertProfessionCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Title is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 100.")
                .MustAsync(BeUniqueTitle).WithMessage("The specified title already exists.");
        }

        public async Task<bool> BeUniqueTitle(string name, CancellationToken cancellationToken)
        {
            return await _repository.Profession.GetProfessions("","")
                .AllAsync(l => l.Name != name);
        }
    }
}
