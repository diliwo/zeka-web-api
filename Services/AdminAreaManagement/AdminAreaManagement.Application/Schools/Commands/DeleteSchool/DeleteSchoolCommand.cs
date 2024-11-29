using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Schools.Commands.DeleteSchool
{
    public class DeleteSchoolCommand : IRequest<Unit>
    {
        public int SchoolId { get; set; }

        public class DeleteSchoolCommandHandler : IRequestHandler<DeleteSchoolCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteSchoolCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteSchoolCommand request, CancellationToken cancellationToken)
            {
                School entity = _repository.School.GetSchoolById(request.SchoolId);

                if (entity != null)
                {
                    if (entity.Softdelete)
                    {
                        entity.Softdelete = false;
                    }
                    else
                    {
                        entity.Softdelete = true;
                    }

                    _repository.School.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(School), request.SchoolId);
                }

                return Unit.Value;
            }
        }

    }
}