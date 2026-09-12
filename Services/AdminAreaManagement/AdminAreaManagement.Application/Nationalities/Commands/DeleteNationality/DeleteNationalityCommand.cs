using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Nationalities.Commands.DeleteNationality
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("Platform.ReferenceData.Manage")]
    public class DeleteNationalityCommand : IRequest<Unit>
    {
        public int Id{ get; set; }

        public class DeleteCityCommandHandler : IRequestHandler<DeleteNationalityCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteNationalityCommand request, CancellationToken cancellationToken)
            {
                Nationality entity = _repository.Nationality.GetById(request.Id);

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

                    await _repository.Nationality.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(City), request.Id);
                }

                return Unit.Value;
            }
        }

    }
}
