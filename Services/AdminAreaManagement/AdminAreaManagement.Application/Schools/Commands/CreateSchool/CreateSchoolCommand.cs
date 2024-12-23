using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Schools.Commands.CreateSchool
{
    public class CreateSchoolCommand : IRequest<int>
    {
        public string Name { get; set; }
        public string Locality { get; set; }

        public class CreateSchoolCommandHandler : IRequestHandler<CreateSchoolCommand, int>
        {
            private IRepositoryManager _repository;

            public CreateSchoolCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(CreateSchoolCommand request, CancellationToken cancellationToken)
            {
                School entity = new School(request.Name, request.Locality);

                _repository.School.Persist(entity);

                _repository.Save();

                return entity.Id;
            }
        }

    }
}