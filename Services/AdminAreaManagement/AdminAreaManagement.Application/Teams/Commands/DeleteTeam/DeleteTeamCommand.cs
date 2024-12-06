using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Teams.Commands.DeleteTeam
{
    public class DeleteTeamCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteTeamCommandHandler : IRequestHandler<DeleteTeamCommand>
        {
            private readonly IRepositoryManager _repository;

            public DeleteTeamCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task Handle(DeleteTeamCommand request, CancellationToken cancellationToken)
            {
                var foundedservice = _repository.Team.Get(request.Id);


                if (foundedservice != null)
                {
                    if (foundedservice.Softdelete)
                    {
                        foundedservice.Softdelete = false;
                    }
                    else
                    {
                        foundedservice.Softdelete = true;
                    }

                    _repository.Team.SoftDelete(foundedservice);

                    _repository.Save();
                }
                else
                {
                    throw new NotFoundException(nameof(Team), request.Id);
                }
            }
        }
    }
}
