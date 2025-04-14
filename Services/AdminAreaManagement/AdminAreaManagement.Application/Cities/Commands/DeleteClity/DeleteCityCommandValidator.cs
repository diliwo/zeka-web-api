using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Cities.Commands.DeleteClity
{
    public class DeleteCityCommandValidator : AbstractValidator<DeleteCityCommand>
    {

        private readonly IRepositoryManager _repository;

        public DeleteCityCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.CityId)
                .NotEmpty().WithMessage("CityId is required.");
        }
    }
}
