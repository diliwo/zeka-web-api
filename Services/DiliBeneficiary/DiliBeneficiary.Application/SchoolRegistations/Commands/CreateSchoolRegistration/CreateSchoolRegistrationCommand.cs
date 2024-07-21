using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.SchoolRegistations.Commands.CreateSchoolRegistration
{
    public class CreateSchoolRegistrationCommand : IRequest<int>
    {
        public int FormationId { get; set; }
        public int SchoolId { get; set; }
        public int TrainingTypeId { get; set; }
        public int BeneficiaryId { get; set; }
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

        //        var beneficiary = _repository.Beneficiary.Get(request.BeneficiaryId, true);

        //        if (beneficiary == null)
        //        {
        //            throw new NotFoundException(nameof(Beneficiary), request.BeneficiaryId);
        //        }

        //        SchoolRegistration entity = new SchoolRegistration(formation, school, trainingType, beneficiary,request.CourseLevel, request.Result, request.StartDate.ToLocalTime(), request.EnDate.ToLocalTime(), request.Note);

        //        _repository.SchoolRegistration.Persist(entity);

        //        return entity.Id;
        //    }
        //}

    }
}