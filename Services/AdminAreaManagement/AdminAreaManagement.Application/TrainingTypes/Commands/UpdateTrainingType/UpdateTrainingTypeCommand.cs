using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.TrainingTypes.Commands.UpdateTrainingType
{
    public class UpdateTrainingTypeCommand : IRequest<int>
    {
        public int Id { get; set; }
        public string Name { get; set; }

        public class UpdateTrainingTypeCommandHandler : IRequestHandler<UpdateTrainingTypeCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdateTrainingTypeCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(UpdateTrainingTypeCommand request, CancellationToken cancellationToken)
            {

                var foundedTrainingType = _repository.TrainingType.GetById(request.Id);

                if (foundedTrainingType == null)
                {
                    throw new NotFoundException(nameof(TrainingType), request.Id);
                }

                foundedTrainingType.Name = request.Name;

                _repository.TrainingType.Persist(foundedTrainingType);

                return foundedTrainingType.Id;
            }
        }

    }
}