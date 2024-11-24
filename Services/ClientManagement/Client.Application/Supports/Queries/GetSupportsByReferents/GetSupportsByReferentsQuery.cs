using AutoMapper;
using Client.Application.Common.Exceptions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Core.Common.Dto;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Supports.Queries.GetSupportsByStaffs
{
    public class GetSupportsByStaffsQuery : IRequest<PaginatedList<MyConsultantSupportDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool IsActive { get; set; }
        public string OrderBy { get; set; }


        public class GetClientsByStaffsQueryHandler : IRequestHandler<GetSupportsByStaffsQuery, PaginatedList<MyConsultantSupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<MyConsultantSupportDto> _sortMyConsultantSupports;
            //private readonly IHttpContextAccessor _httpContextAccessor;
            private readonly IMapper _mapper;

            public GetClientsByStaffsQueryHandler(
                IRepositoryManager repository, 
                //IHttpContextAccessor httpContextAccessor,
                ISortHelper<MyConsultantSupportDto> sortMyConsultantSupports,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyConsultantSupports = sortMyConsultantSupports;
                //_httpContextAccessor = httpContextAccessor;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MyConsultantSupportDto>> Handle(GetSupportsByStaffsQuery request, CancellationToken cancellationToken)
            {
                //var StaffUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
                var StaffUserName = "System";
                if (StaffUserName == null)
                {
                    throw new NotFoundException(nameof(StaffUserName), StaffUserName);
                }

                    var supports = _sortMyConsultantSupports.ApplySort(_repository.Support.GetConsultantSupportsByUserName(StaffUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
