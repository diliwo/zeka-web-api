using AutoMapper;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Queries.GetSupportsByReferents
{
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
            //private readonly IHttpContextAccessor _httpContextAccessor;
            private readonly IMapper _mapper;

            public GetClientsByStaffMembersQueryHandler(
                IRepositoryManager repository, 
                //IHttpContextAccessor httpContextAccessor,
                ISortHelper<MySupportDto> sortMyConsultantSupports,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyConsultantSupports = sortMyConsultantSupports;
                //_httpContextAccessor = httpContextAccessor;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MySupportDto>> Handle(GetSupportsBySocialWorkersQuery request, CancellationToken cancellationToken)
            {
                //var StaffMemberUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
                var StaffMemberUserName = "System";
                if (StaffMemberUserName == null)
                {
                    throw new NotFoundException(nameof(StaffMemberUserName), StaffMemberUserName);
                }

                    var supports = _sortMyConsultantSupports.ApplySort(_repository.Support.GetConsultantSupportsByUserName(StaffMemberUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
