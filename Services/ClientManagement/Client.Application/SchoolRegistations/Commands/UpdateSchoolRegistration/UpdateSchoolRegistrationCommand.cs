using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.SchoolRegistations.Commands.UpdateSchoolRegistration
{
    public class UpdateSchoolRegistrationCommand : IRequest<int>
    {
        public int SchoolRegistrationId { get; set; }
        public int TrainingId { get; set; }
        public int SchoolId { get; set; }
        public int TrainingTypeId { get; set; }
        public int ClientId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        public class UpdateSchoolRegistrationCommandHandler : IRequestHandler<UpdateSchoolRegistrationCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdateSchoolRegistrationCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpdateSchoolRegistrationCommand request, CancellationToken cancellationToken)
            {
                var training = _repository.Training.GetTrainingById(request.TrainingId);

                if (training == null)
                {
                    throw new NotFoundException(nameof(Training), request.TrainingId);
                }


                //TODO: implement the Grpc request here 
                //var school = _repository.School.GetSchoolById(request.SchoolId);
                var school = new School("temp", "tp");

                if (school == null)
                {
                    throw new NotFoundException(nameof(School), request.SchoolId);
                }

                var trainingType = _repository.TrainingType.GetById(request.TrainingTypeId);

                if (trainingType == null)
                {
                    throw new NotFoundException(nameof(TrainingType), request.TrainingTypeId);
                }

                var client = _repository.Client.Get(request.ClientId);

                if (client == null)
                {
                    throw new NotFoundException(nameof(client), request.ClientId);
                }

                var registration = _repository.SchoolRegistration.GetRegistrationById((int)request.SchoolRegistrationId);

                if (registration == null)
                {
                    throw new NotFoundException(nameof(registration), request.SchoolRegistrationId);
                }

                registration.Client = client;
                registration.Training = training;
                registration.TrainingType = trainingType;
                registration.School = school;
                registration.StartDate = request.StartDate.ToLocalTime();
                registration.EnDate = request.EnDate.ToLocalTime();
                registration.CourseLevel = request.CourseLevel;
                registration.Result = request.Result;
                registration.Note = request.Note;

                _repository.SchoolRegistration.Persist(registration);

                return registration.Id;
            }
        }

    }
}