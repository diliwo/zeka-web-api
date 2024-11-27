using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Clients.Queries.GetClients
{
    public class GetClientsQuery : IRequest<ClientsDto>
    {
        public class GetClientsQueryHandler : IRequestHandler<GetClientsQuery, ClientsDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientsQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<ClientsDto> Handle(GetClientsQuery query, CancellationToken cancellationToken)
            {
                var Clients = await _repository.Client.GetClients()
                    .ProjectTo<ClientLookUpDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.Name)
                    .ToListAsync(cancellationToken);

                var vm = new ClientsDto
                {
                    Clients = Clients
                };

                return vm;
            }
        }

    }
}
