using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Queries
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetSupportsListByClientQuery : IRequest<PaginatedList<SupportDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; }
        public string SortOrder { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }


        public class GetTracksListByClientQueryHandler : IRequestHandler<GetSupportsListByClientQuery, PaginatedList<SupportDto>>
        {
            private readonly IRepositoryManager _repository;

            public GetTracksListByClientQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<PaginatedList<SupportDto>> Handle(GetSupportsListByClientQuery request, CancellationToken cancellationToken)
            {
                var cases = (IQueryable<SocialCase>)_repository.Support.GetSupportsByClientId(request.ClientId);
                var supports = await cases.ProjectSupports()
                    .OrderBy(s => s.StartDate)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);

                return supports;
            }
        }
    }
}
