using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Staffs.Queries
{
    public class GetStaffMemberListQuery : IRequest<PaginatedList<StaffMemberDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetReferentsListQueryHandler : IRequestHandler<GetStaffMemberListQuery, PaginatedList<StaffMemberDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<StaffMemberDto> _sortReferent;
            private readonly IMapper _mapper;

            public GetReferentsListQueryHandler(
                IRepositoryManager repository,
                ISortHelper<StaffMemberDto> sortReferent,
                IMapper mapper
                )
            {
                _repository = repository;
                _sortReferent = sortReferent;
                _mapper = mapper;
            }

            public async Task<PaginatedList<StaffMemberDto>> Handle(GetStaffMemberListQuery request, CancellationToken cancellationToken)
            {
                 var query = 
                     _sortReferent.ApplySort(_repository.StaffMember.GetStaffMembers(request.Filter).ProjectTo<StaffMemberDto>(_mapper.ConfigurationProvider), request.OrderBy);

                 return await query.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
