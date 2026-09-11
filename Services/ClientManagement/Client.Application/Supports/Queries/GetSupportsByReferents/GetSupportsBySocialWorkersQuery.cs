using AutoMapper;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using MediatR;
using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Application.Supports.Queries.GetSupportsByReferents
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetSupportsBySocialWorkersQuery : IRequest<PaginatedList<MySupportDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool IsActive { get; set; }
        public string OrderBy { get; set; }


        public class GetClientsByStaffMembersQueryHandler : IRequestHandler<GetSupportsBySocialWorkersQuery, PaginatedList<MySupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<MySupportDto> _sortMyConsultantSupports;
            private readonly TenantOperation _operation;
            private readonly IMapper _mapper;

            public GetClientsByStaffMembersQueryHandler(
                IRepositoryManager repository,
                TenantOperation operation,
                ISortHelper<MySupportDto> sortMyConsultantSupports,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyConsultantSupports = sortMyConsultantSupports;
                _operation = operation;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MySupportDto>> Handle(GetSupportsBySocialWorkersQuery request, CancellationToken cancellationToken)
            {
                if (_operation.MembershipId == Guid.Empty)
                {
                    throw new TenantAccessException(AccessFailure.Denied);
                }

                var supports = _sortMyConsultantSupports.ApplySort(_repository.Support.GetConsultantSupportsByMembership(_operation.MembershipId, request.Filter, request.IsActive), request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
