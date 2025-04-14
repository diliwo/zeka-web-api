using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Cities.Commands.CreateCity
{
    public class CreateCityCommandValidator : AbstractValidator<CreateCityCommand>
    {

        private readonly IRepositoryManager _repository;

        public CreateCityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Name is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 50.")
                .MustAsync(BeUnique).WithMessage("The specified city already exists.");

            RuleFor(v => v.Country)
                .NotEmpty().WithMessage("Country is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 50.")
                .MustAsync(BeUnique).WithMessage("The specified country already exists.");
        }

        public async Task<bool> BeUnique(string name, CancellationToken cancellationToken)
        {
            return await _repository.City.GetCities("")
                .AllAsync(c => c.Name != name || c.Country != name);
        }
    }
}
