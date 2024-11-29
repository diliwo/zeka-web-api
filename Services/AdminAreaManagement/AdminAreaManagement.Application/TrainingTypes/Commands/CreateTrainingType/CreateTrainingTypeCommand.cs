using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.TrainingTypes.Commands.CreateTrainingType
{
    public class CreateTrainingTypeCommand : IRequest<int>
    {
        public string Name { get; set; }

        public class CreateTrainingTypeCommandHandler : IRequestHandler<CreateTrainingTypeCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public CreateTrainingTypeCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(CreateTrainingTypeCommand request, CancellationToken cancellationToken)
            {
                TrainingType type = new TrainingType(request.Name);

                _repository.TrainingType.Persist(type);

                return type.Id;
            }
        }

    }
}