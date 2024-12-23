using AutoMapper;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Tracks.Queries.GetSupportsByReferents
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
                //var StaffMemberUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
                var StaffMemberUserName = "System";
                if (StaffMemberUserName == null)
                {
                    throw new NotFoundException(nameof(StaffMemberUserName), StaffMemberUserName);
                }

                var supports = _sortMyJobCoachSupports.ApplySort(_repository.Track.GetJobCoachSupportsByUserName(StaffMemberUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
