using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Queries
{
    public class GetSupportsListByBeneficiaryQuery : IRequest<PaginatedList<SupportDto>>
    {
        public int BeneficiaryId { get; set; }
        public string Filter { get; set; }
        public string SortOrder { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }


        public class GetSupportListByBeneficiaryQueryHandler : IRequestHandler<GetSupportsListByBeneficiaryQuery, PaginatedList<SupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetSupportListByBeneficiaryQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<SupportDto>> Handle(GetSupportsListByBeneficiaryQuery request, CancellationToken cancellationToken)
            {
                //if (request.BeneficiaryId == null)
                //{
                //    throw new BadHttpRequestException("Beneficiary",request.BeneficiaryId);
                //}

                var supports = await _repository.Support.GetSupportsByBeneficiaryId(request.BeneficiaryId)
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);

                return supports;
            }
        }
    }
}
