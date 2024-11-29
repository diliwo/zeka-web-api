using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Formations.Commands.CreateFormation
{
    public class CreateTrainingCommand : IRequest<int>
    {
        public int TrainingId { get; set; }
        public string Name { get; set; }
        public int TrainingFieldId { get; set; }

        public class CreateTrainingCommandHandler : IRequestHandler<CreateTrainingCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public CreateTrainingCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(CreateTrainingCommand request, CancellationToken cancellationToken)
            {
                var field = _repository.TrainingField.GetById(request.TrainingFieldId);

                if (field == null)
                {
                    throw new NotFoundException(nameof(Training), request.TrainingFieldId);
                }

                Training entity = new Training(request.Name, field);

                _repository.Trainings.Persist(entity);

                return entity.Id;
            }
        }

    }
}