using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.SchoolRegistations.Commands.UpdateSchoolRegistration
{
    public class UpdateSchoolRegistrationCommand : IRequest<int>
    {
        public int SchoolRegistrationId { get; set; }
        public int FormationId { get; set; }
        public int SchoolId { get; set; }
        public int TrainingTypeId { get; set; }
        public int BeneficiaryId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EnDate { get; set; }
        public Level CourseLevel { get; set; }
        public SchoolResult Result { get; set; }
        public string? Note { get; set; }

        //public class UpdateSchoolRegistrationCommandHandler : IRequestHandler<UpdateSchoolRegistrationCommand, int>
        //{
        //    private readonly IRepositoryManager _repository;

        //    public UpdateSchoolRegistrationCommandHandler(
        //        IRepositoryManager repository
        //    )
        //    {
        //        _repository = repository;
        //    }

        //    public async Task<int> Handle(UpdateSchoolRegistrationCommand request, CancellationToken cancellationToken)
        //    {
        //        var formation = _repository.Formation.GetFormationById(request.FormationId);

        //        if (formation == null)
        //        {
        //            throw new NotFoundException(nameof(Formation), request.FormationId);
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

        //        var beneficiary = _repository.Beneficiary.Get(request.BeneficiaryId);

        //        if (beneficiary == null)
        //        {
        //            throw new NotFoundException(nameof(beneficiary), request.BeneficiaryId);
        //        }

        //        var registration = _repository.SchoolRegistration.GetRegistrationById((int)request.SchoolRegistrationId);

        //        if (registration == null)
        //        {
        //            throw new NotFoundException(nameof(registration), request.SchoolRegistrationId);
        //        }

        //        registration.Beneficiary = beneficiary;
        //        registration.Formation = formation;
        //        registration.TrainingType = trainingType;
        //        registration.School = school;
        //        registration.StartDate = request.StartDate.ToLocalTime();
        //        registration.EnDate = request.EnDate.ToLocalTime();
        //        registration.CourseLevel = request.CourseLevel;
        //        registration.Result = request.Result;
        //        registration.Note = request.Note;
                
        //        _repository.SchoolRegistration.Persist(registration);

        //        return registration.Id;
        //    }
        //}

    }
}