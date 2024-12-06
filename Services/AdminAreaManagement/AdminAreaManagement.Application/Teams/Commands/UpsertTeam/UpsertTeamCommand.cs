using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Teams.Commands.UpsertTeam
{
    public class UpsertTeamCommand : IRequest<int>
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public string Acronym { get; set; }

        public class UpsertTeamCommandHandler : IRequestHandler<UpsertTeamCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpsertTeamCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpsertTeamCommand request, CancellationToken cancellationToken)
            {
                Team entity;

                if (request.Id.HasValue)
                {
                    entity = _repository.Team.Get(request.Id.Value);
                    entity.Acronym = request.Acronym.Trim();
                    entity.Name = request.Name.Trim();

                    Console.WriteLine("New Value : " + request.Acronym + " - " + request.Name);
                }
                else
                {
                    entity = new Team(request.Name, request.Acronym);
                }

                _repository.Team.Persist(entity);

                _repository.Save();

                return entity.Id;
            }
        }
    }
}
