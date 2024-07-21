using AutoMapper;
using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Core.Common.Dto;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace DiliBeneficiary.Application.Supports.Queries.GetSupportsByReferents
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
            private readonly IHttpContextAccessor _httpContextAccessor;
            private readonly IMapper _mapper;

            public GetSupportsByJobCoachQueryHandler(
                IRepositoryManager repository,
                ISortHelper<MyJobCoachSupportDto> sortMyJobCoachSupports,
                IHttpContextAccessor httpContextAccessor,
                IMapper mapper)
            {
                _repository = repository;
                _sortMyJobCoachSupports = sortMyJobCoachSupports;
                _httpContextAccessor = httpContextAccessor;
                _mapper = mapper;
            }

            public async Task<PaginatedList<MyJobCoachSupportDto>> Handle(GetSupportsByJobCoachQuery request, CancellationToken cancellationToken)
            {
                var referentUserName = _httpContextAccessor.HttpContext.User.Identity.Name;

                if (referentUserName == null)
                {
                    throw new NotFoundException(nameof(referentUserName), referentUserName);
                }

                var supports = _sortMyJobCoachSupports.ApplySort(_repository.Support.GetJobCoachSupportsByUserName(referentUserName,request.Filter, request.IsActive),request.OrderBy);

                return await supports.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
