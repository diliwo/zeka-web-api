using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Professions.Commands.DeleteProfession
{
    public class DeleteProfessionCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteProfessionCommandHandler : IRequestHandler<DeleteProfessionCommand>
        {
            private readonly IRepositoryManager _repository;

            public DeleteProfessionCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task Handle(DeleteProfessionCommand request, CancellationToken cancellationToken)
            {
                var foundedProfession = _repository.Profession.Get(request.Id);


                if (foundedProfession != null)
                {
                    if (foundedProfession.Softdelete)
                    {
                        foundedProfession.Softdelete = false;
                    }
                    else
                    {
                        foundedProfession.Softdelete = true;
                    }

                    _repository.Profession.SoftDelete(foundedProfession);
                }
                else
                {
                    throw new NotFoundException(nameof(Profession), request.Id);
                }
            }
        }
    }
}
