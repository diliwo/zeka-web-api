using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Partners.Queries.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Partners.Queries.GetPartnersName
{
    public class GetPartnersSelectionListQuery : IRequest<PaginatedList<PartnerSelectionListDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetPartnersSelectionListQueryHandler : IRequestHandler<GetPartnersSelectionListQuery, PaginatedList<PartnerSelectionListDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<PartnerSelectionListDto> _sortPartner;
            private readonly IMapper _mapper;

            public GetPartnersSelectionListQueryHandler(IRepositoryManager repository, ISortHelper<PartnerSelectionListDto> sortPartner, IMapper mapper)
            {
                _repository = repository;
                _sortPartner = sortPartner;
                _mapper = mapper;
            }

            public async Task<PaginatedList<PartnerSelectionListDto>> Handle(GetPartnersSelectionListQuery query, CancellationToken cancellationToken)
            {
                var partners = _sortPartner.ApplySort(_repository.Partner.GetPartners(query.Filter, true)
                    .ProjectTo<PartnerSelectionListDto>(_mapper.ConfigurationProvider),query.OrderBy);

                 return await partners.PaginatedListAsync(query.PageNumber, query.PageSize);
            }
        }
    }
}
