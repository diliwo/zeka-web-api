using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Professions.Commands.UpsertProfession
{
    public class UpsertProfessionCommand : IRequest<int>
    {
        public int? Id { get; set; }
        public string Name { get; set; }

        public class UpsertProfessionCommandHandler : IRequestHandler<UpsertProfessionCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpsertProfessionCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpsertProfessionCommand request, CancellationToken cancellationToken)
            {
                Profession entity;

                if (request.Id.HasValue)
                {
                    entity = _repository.Profession.Get(request.Id.Value);
                    entity.Name = request.Name.Trim();
                }
                else
                {
                    entity = new Profession(request.Name);
                }

                _repository.Profession.Persist(entity);

                return entity.Id;
            }
        }
    }
}
