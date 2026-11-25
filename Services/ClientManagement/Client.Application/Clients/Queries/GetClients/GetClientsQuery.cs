using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Clients.Queries.GetClients
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetClientsQuery : IRequest<ClientsDto>
    {
        public class GetClientsQueryHandler : IRequestHandler<GetClientsQuery, ClientsDto>
        {
            private readonly IRepositoryManager _repository;

            public GetClientsQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<ClientsDto> Handle(GetClientsQuery query, CancellationToken cancellationToken)
            {
                var Clients = await _repository.Client.GetClients()
                    .ProjectLookup()
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
