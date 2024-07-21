using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Bilans.Common;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Bilans.Queries.GetBilansList
{
    public class GetBilansListQuery : IRequest<PaginatedList<BilanDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int BeneficiaryId { get; set; }

        public class GetBilansListQueryHandler : IRequestHandler<GetBilansListQuery, PaginatedList<BilanDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBilansListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<BilanDto>> Handle(GetBilansListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Bilan.GetBilans(request.BeneficiaryId)
                    .Include(i => i.BilanProfessions)
                    .ProjectTo<BilanDto>(_mapper.ConfigurationProvider)
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
