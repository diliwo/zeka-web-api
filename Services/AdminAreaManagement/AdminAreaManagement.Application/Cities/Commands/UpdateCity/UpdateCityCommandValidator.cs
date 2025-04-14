using AdminAreaManagement.Application.Cities.Commands.CreateCity;
using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Cities.Commands.UpdateCity
{
    public class UpdateCityCommandValidator : AbstractValidator<UpdateCityCommand>
    {

        private readonly IRepositoryManager _repository;

        public UpdateCityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.CityId)
                .NotEmpty().WithMessage("CityId is required.");
        }
    }
}
