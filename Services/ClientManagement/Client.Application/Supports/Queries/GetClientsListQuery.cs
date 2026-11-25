using ClientManagement.Application.Common.Mappings;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Supports.Queries
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetClientsListQuery : IRequest<SupportsListVm>
    {
        public class GetSupportsListQueryHandler : IRequestHandler<GetClientsListQuery, SupportsListVm>
        {
            private readonly IRepositoryManager _repository;


            public GetSupportsListQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<SupportsListVm> Handle(GetClientsListQuery request, CancellationToken cancellationToken)
            {
                var supports = await _repository.Support.GetSupports()
                    .ProjectSupports()
                    .OrderBy(s => s.StartDate)
                    .ToListAsync(cancellationToken);

                var vm = new SupportsListVm
                {
                    Tracks = supports
                };

                return vm;
            }
        }
    }
}
