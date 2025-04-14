using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Commands.DeleteClity
{
    public class DeleteCityCommand : IRequest<Unit>
    {
        public int CityId { get; set; }

        public class DeleteCityCommandHandler : IRequestHandler<DeleteCityCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteCityCommand request, CancellationToken cancellationToken)
            {
                City entity = _repository.City.GetById(request.CityId);

                if (entity != null)
                {
                    if (entity.Softdelete)
                    {
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
                    throw new NotFoundException(nameof(City), request.CityId);
                }

                return Unit.Value;
            }
        }

    }
}