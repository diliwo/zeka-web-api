using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Nationalities.Commands.CreateCity
{
    public class CreateNationalityCommandValidator : AbstractValidator<CreateNationalityCommand>
    {

        private readonly IRepositoryManager _repository;

        public CreateNationalityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Name is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 50.")
                .MustAsync(BeUnique).WithMessage("The specified city already exists.");
        }

        public async Task<bool> BeUnique(string name, CancellationToken cancellationToken)
        {
            return await _repository.Nationality.GetNationalities("")
                .AllAsync(c => c.Name != name);
        }
    }
}
