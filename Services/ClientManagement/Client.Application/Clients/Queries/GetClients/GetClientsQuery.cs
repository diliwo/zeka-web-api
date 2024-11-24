using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Clients.Queries.GetClients
{
    public class GetClientsQuery : IRequest<ClientsVm>
    {
        public class GetClientsQueryHandler : IRequestHandler<GetClientsQuery, ClientsVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientsQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<ClientsVm> Handle(GetClientsQuery query, CancellationToken cancellationToken)
            {
                var Clients = await _repository.Client.GetClients()
                    .ProjectTo<ClientLookUpDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.Name)
                    .ToListAsync(cancellationToken);

                var vm = new ClientsVm
                {
                    Clients = Clients
                };

                return vm;
            }
        }

    }
}
