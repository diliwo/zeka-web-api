using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Formations.Commands.UpdateFormation
{
    public class UpdateTrainingCommand : IRequest<int>
    {
        public int TrainingId { get; set; }
        public string Name { get; set; }
        public int TrainingFieldId { get; set; }

        public class UpdateTrainingCommandHandler : IRequestHandler<UpdateTrainingCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdateTrainingCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(UpdateTrainingCommand request, CancellationToken cancellationToken)
            {

                var foundedTraining = _repository.Trainings.GetById(request.TrainingId);

                if (foundedTraining == null)
                {
                    throw new NotFoundException(nameof(Training), request.TrainingId);
                }

                var foundedTrainingField = _repository.TrainingField.GetById(request.TrainingFieldId);

                if (foundedTrainingField == null)
                {
                    throw new NotFoundException(nameof(TrainingField), request.TrainingFieldId);
                }

                foundedTraining.Name = request.Name;
                foundedTraining.TrainingField = foundedTrainingField;

                _repository.Trainings.Persist(foundedTraining);

                return foundedTraining.Id;
            }
        }

    }
}