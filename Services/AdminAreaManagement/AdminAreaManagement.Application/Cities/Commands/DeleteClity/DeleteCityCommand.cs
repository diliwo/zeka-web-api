using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using FluentValidation.Results;

namespace AdminAreaManagement.Application.Cities.Commands.DeleteClity
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("Platform.ReferenceData.Manage")]
    public class DeleteCityCommand : IRequest<Unit>
    {
        public int Id { get; set; }

        public class DeleteCityCommandHandler : IRequestHandler<DeleteCityCommand, Unit>
        {
            private IRepositoryManager _repository;
            private readonly ICityQueries _cities;

            public DeleteCityCommandHandler(IRepositoryManager repository, ICityQueries cities)
            {
                _repository = repository;
                _cities = cities;
            }
            public async Task<Unit> Handle(DeleteCityCommand request, CancellationToken cancellationToken)
            {
                City entity = _repository.City.GetById(request.Id);

                if (entity != null)
                {
                    if (entity.Softdelete)
                    {
                        if (await _cities.ActiveCityExistsAsync(entity.Name, entity.Country, cancellationToken))
                            throw new ValidationException(new[]
                            {
                                new ValidationFailure(nameof(City.Name), "The specified city already exists.")
                            });
                        entity.Softdelete = false;
                    }
                    else
                    {
                        entity.Softdelete = true;
                    }

                    _repository.City.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(City), request.Id);
                }

                return Unit.Value;
            }
        }

    }
}
