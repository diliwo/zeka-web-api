﻿using Client.Core.Enums;
using MediatR;

namespace Client.Application.SchoolRegistations.Commands.CreateSchoolRegistration
{
    public class CreateSchoolRegistrationCommand : IRequest<int>
    {
        public int TrainingId { get; set; }
        public int SchoolId { get; set; }
        public int TrainingTypeId { get; set; }
        public int ClientId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        //public class CreateSchoolRegistrationCommandHandler : IRequestHandler<CreateSchoolRegistrationCommand, int>
        //{
        //    private readonly IRepositoryManager _repository;

        //    public CreateSchoolRegistrationCommandHandler(
        //        IRepositoryManager repository
        //    )
        //    {
        //        _repository = repository;

        //    }
        //    public async Task<int> Handle(CreateSchoolRegistrationCommand request, CancellationToken cancellationToken)
        //    {
        //        var training = _repository.Training.GetTrainingById(request.TrainingId);

        //        if (training == null)
        //        {
        //            throw new NotFoundException(nameof(Training), request.TrainingId);
        //        }

        //        var school = _repository.School.GetSchoolById(request.SchoolId);

        //        if (school == null)
        //        {
        //            throw new NotFoundException(nameof(School), request.SchoolId);
        //        }

        //        var trainingType = _repository.TrainingType.GetById(request.TrainingTypeId);
        //        if (trainingType == null)
        //        {
        //            throw new NotFoundException(nameof(TrainingType), request.TrainingTypeId);
        //        }

        //        var client = _repository.Client.Get(request.ClientId, true);

        //        if (client == null)
        //        {
        //            throw new NotFoundException(nameof(Client), request.ClientId);
        //        }

        //        SchoolEnrollment entity = new SchoolEnrollment(training, school, trainingType, client,request.CourseLevel, request.Result, request.StartDate.ToLocalTime(), request.EnDate.ToLocalTime(), request.Note);

        //        _repository.SchoolEnrollment.Persist(entity);

        //        return entity.Id;
        //    }
        //}

    }
}