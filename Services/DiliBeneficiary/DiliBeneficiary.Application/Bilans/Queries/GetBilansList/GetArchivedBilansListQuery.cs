using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Bilans.Common;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Bilans.Queries.GetBilansList
{
    public class GetArchivedBilansListQuery : IRequest<PaginatedList<BilanDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetArchivedBilansListQueryHandler : IRequestHandler<GetArchivedBilansListQuery, PaginatedList<BilanDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetArchivedBilansListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<BilanDto>> Handle(GetArchivedBilansListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Bilan.GetArchivedBilans()
                    .ProjectTo<BilanDto>(_mapper.ConfigurationProvider)
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
