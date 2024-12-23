using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using MediatR;

namespace AdminAreaManagement.Application.Teams.Queries
{
    public class GetTeamsByNameQuery : IRequest<Boolean>
    {
        public string Name{ get; set; }

        public class GetServicesByNameQueryHandler : IRequestHandler<GetTeamsByNameQuery, Boolean>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetServicesByNameQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<Boolean> Handle(GetTeamsByNameQuery request, CancellationToken cancellationToken)
            {
                var vm = _repository.Team.IsTeamUnique(request.Name.Trim());

                if (vm)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
