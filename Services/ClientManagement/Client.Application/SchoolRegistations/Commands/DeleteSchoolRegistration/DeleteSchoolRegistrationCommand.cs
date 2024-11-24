using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.SchoolRegistations.Commands.DeleteSchoolRegistration
{
    public class DeleteSchoolRegistrationCommand : IRequest<Unit>
    {
        public int SchoolRegistrationId { get; set; }

        public class DeleteSchoolCommandHandler : IRequestHandler<DeleteSchoolRegistrationCommand, Unit>
        {
            private readonly IRepositoryManager _repository;

            public DeleteSchoolCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteSchoolRegistrationCommand request, CancellationToken cancellationToken)
            {
                var entity = _repository.SchoolRegistration.GetRegistrationById(request.SchoolRegistrationId);

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

                    _repository.SchoolRegistration.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(SchoolEnrollment), request.SchoolRegistrationId);
                }

                return Unit.Value;
            }
        }

    }
}