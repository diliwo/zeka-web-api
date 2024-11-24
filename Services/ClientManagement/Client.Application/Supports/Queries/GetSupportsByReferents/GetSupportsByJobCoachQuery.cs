using AutoMapper;
using Client.Application.Common.Exceptions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Core.Common.Dto;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Supports.Queries.GetSupportsByStaffs
{
    public class GetSupportsByJobCoachQuery : IRequest<PaginatedList<MyJobCoachSupportDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool IsActive {get; set; }
        public string OrderBy { get; set; }

        public class GetSupportsByJobCoachQueryHandler : IRequestHandler<GetSupportsByJobCoachQuery, PaginatedList<MyJobCoachSupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<MyJobCoachSupportDto> _sortMyJobCoachSupports;
           // private readonly IHttpContextAccessor _httpContextAccessor;
            private readonly IMapper _mapper;

            public GetSupportsByJobCoachQueryHandler(
                IRepositoryManager repository,
                ISortHelper<MyJobCoachSupportDto> sortMyJobCoachSupports,
                //IHttpContextAccessor httpContextAccessor,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyJobCoachSupports = sortMyJobCoachSupports;
                //_httpContextAccessor = httpContextAccessor;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MyJobCoachSupportDto>> Handle(GetSupportsByJobCoachQuery request, CancellationToken cancellationToken)
            {
                //var StaffUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
                var StaffUserName = "System";
                if (StaffUserName == null)
                {
                    throw new NotFoundException(nameof(StaffUserName), StaffUserName);
                }

                var supports = _sortMyJobCoachSupports.ApplySort(_repository.Support.GetJobCoachSupportsByUserName(StaffUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
