using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using MediatR;

namespace AdminAreaManagement.Application.Services.Queries
{
    public class GetServicesByNameQuery : IRequest<Boolean>
    {
        public string Name{ get; set; }

        public class GetServicesByNameQueryHandler : IRequestHandler<GetServicesByNameQuery, Boolean>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetServicesByNameQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<Boolean> Handle(GetServicesByNameQuery request, CancellationToken cancellationToken)
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
