using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Formations.Commands.DeleteFormation
{
    public class DeleteTrainingCommand : IRequest<Unit>
    {
        public int Id { get; set; }

        public class DeleteTrainingCommandHandler : IRequestHandler<DeleteTrainingCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteTrainingCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteTrainingCommand request, CancellationToken cancellationToken)
            {
                Training entity = _repository.Trainings.GetById(request.Id);

                if (entity is not null)
                {
                    if (entity.Softdelete)
                    {
                        entity.Softdelete = false;
                    }
                    else
                    {
                        entity.Softdelete = true;
                    }

                    _repository.Trainings.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(Training), request.Id);
                }

                return Unit.Value;
            }
        }

    }
}