using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Schools.Commands.UpdateSchool
{
    public class UpdateSchoolCommand : IRequest<int>
    {
        public int? SchoolId { get; set; }
        public string Name { get; set; }
        public string Locality { get; set; }

        public class UpdateSchoolCommandHandler : IRequestHandler<UpdateSchoolCommand, int>
        {
            private IRepositoryManager _repository;

            public UpdateSchoolCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(UpdateSchoolCommand request, CancellationToken cancellationToken)
            {

                if (!request.SchoolId.HasValue)
                {
                    throw new NotFoundException(nameof(School), request.SchoolId);
                }

                School entity = _repository.School.GetSchoolById((int)request.SchoolId);
                entity.Name = request.Name;
                entity.Locality = request.Locality;
                
                _repository.School.Persist(entity);

                return entity.Id;
            }
        }

    }
}