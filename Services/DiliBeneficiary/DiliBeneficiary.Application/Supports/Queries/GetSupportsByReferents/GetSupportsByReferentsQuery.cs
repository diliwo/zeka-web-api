using AutoMapper;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Queries.GetSupportsByReferents
{
    public class GetSupportsByReferentsQuery : IRequest<PaginatedList<MyConsultantSupportDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public bool IsActive { get; set; }
        public string OrderBy { get; set; }


        public class GetBeneficiariesByReferentsQueryHandler : IRequestHandler<GetSupportsByReferentsQuery, PaginatedList<MyConsultantSupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<MyConsultantSupportDto> _sortMyConsultantSupports;
            private readonly IHttpContextAccessor _httpContextAccessor;
            private readonly IMapper _mapper;

            public GetBeneficiariesByReferentsQueryHandler(
                IRepositoryManager repository, 
                IHttpContextAccessor httpContextAccessor,
                ISortHelper<MyConsultantSupportDto> sortMyConsultantSupports,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyConsultantSupports = sortMyConsultantSupports;
                _httpContextAccessor = httpContextAccessor;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MyConsultantSupportDto>> Handle(GetSupportsByReferentsQuery request, CancellationToken cancellationToken)
            {
                var referentUserName = _httpContextAccessor.HttpContext.User.Identity.Name;

                if (referentUserName == null)
                {
                    throw new NotFoundException(nameof(referentUserName), referentUserName);
                }

                var supports = _sortMyConsultantSupports.ApplySort(_repository.Support.GetConsultantSupportsByUserName(referentUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
