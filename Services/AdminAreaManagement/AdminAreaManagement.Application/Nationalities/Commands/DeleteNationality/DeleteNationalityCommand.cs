using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Nationalities.Commands.DeleteNationality
{
    public class DeleteNationalityCommand : IRequest<Unit>
    {
        public int NationalityId { get; set; }

        public class DeleteCityCommandHandler : IRequestHandler<DeleteNationalityCommand, Unit>
        {
            private IRepositoryManager _repository;

            public DeleteCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteNationalityCommand request, CancellationToken cancellationToken)
            {
                Nationality entity = _repository.Nationality.GetById(request.NationalityId);

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

                    _repository.Nationality.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(City), request.NationalityId);
                }

                return Unit.Value;
            }
        }

    }
}