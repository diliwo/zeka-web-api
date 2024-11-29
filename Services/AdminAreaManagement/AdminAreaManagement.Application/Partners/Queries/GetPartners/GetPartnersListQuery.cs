using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Partners.Queries.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Partners.Queries.GetPartners
{
    public class GetPartnersListQuery : IRequest<PaginatedList<PartnerDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetPartnersListQueryHandler : IRequestHandler<GetPartnersListQuery, PaginatedList<PartnerDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<PartnerDto> _sortPartner;
            private readonly IMapper _mapper;

            public GetPartnersListQueryHandler(IRepositoryManager repository, ISortHelper<PartnerDto> sortPartner, IMapper mapper)
            {
                _repository = repository;
                _sortPartner = sortPartner;
                _mapper = mapper;
            }

            public async Task<PaginatedList<PartnerDto>> Handle(GetPartnersListQuery query, CancellationToken cancellationToken)
            {
                var partners = _sortPartner.ApplySort(_repository.Partner.GetPartners(query.Filter, false)
                    .Include(x => x.Documents)
                    .AsNoTracking()
                    .ProjectTo<PartnerDto>(_mapper.ConfigurationProvider),query.OrderBy);

                 return await partners.PaginatedListAsync(query.PageNumber, query.PageSize);
            }
        }
    }
}
