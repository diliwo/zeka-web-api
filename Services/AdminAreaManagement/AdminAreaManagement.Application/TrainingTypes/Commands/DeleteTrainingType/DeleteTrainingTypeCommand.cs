using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.TrainingTypes.Commands.DeleteTrainingType
{
    public class DeleteTrainingTypeCommand : IRequest<Unit>
    {
        public int Id { get; set; }

        public class DeleteTrainingTypeCommandHandler : IRequestHandler<DeleteTrainingTypeCommand, Unit>
        {
            private readonly IRepositoryManager _repository;

            public DeleteTrainingTypeCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteTrainingTypeCommand request, CancellationToken cancellationToken)
            {
                TrainingType entity = _repository.TrainingType.GetById(request.Id);

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

                    _repository.TrainingType.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(TrainingType), request.Id);
                }

                return Unit.Value;
            }
        }

    }
}