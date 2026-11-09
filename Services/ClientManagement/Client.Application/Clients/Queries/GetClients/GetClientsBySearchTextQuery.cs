using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Clients.Queries.GetClients
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetClientsBySearchTextQuery : IRequest<ClientsDto>
    {
        public string SearchText{ get; set; }

        public class GetClientsBySearchTextQueryHandler : IRequestHandler<GetClientsBySearchTextQuery, ClientsDto>
        {
            private readonly IRepositoryManager _repository;

            public GetClientsBySearchTextQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<ClientsDto> Handle(GetClientsBySearchTextQuery query, CancellationToken cancellationToken)
            {
                var Clients = _repository.Client.GetClientsBySearchText(query.SearchText)
                    .ProjectLookup()
                    .OrderBy(b => b.Name)
                    .ToList();

                var vm = new ClientsDto
                {
                    Clients = Clients
                };

                return vm;
            }
        }
    }
}
