using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Commands.DeleteClity
{
    public class DeleteCityCommand : IRequest<Unit>
    {
        public int Id { get; set; }

        public class DeleteCityCommandHandler : IRequestHandler<DeleteCityCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteCityCommand request, CancellationToken cancellationToken)
            {
                City entity = _repository.City.GetById(request.Id);

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
                    throw new NotFoundException(nameof(City), request.Id);
                }

                return Unit.Value;
            }
        }

    }
}