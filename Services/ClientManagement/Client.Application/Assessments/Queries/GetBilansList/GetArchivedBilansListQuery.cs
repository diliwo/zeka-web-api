using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Assessments.Common;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Assessments.Queries.GetBilansList
{
    public class GetArchivedBilansListQuery : IRequest<PaginatedList<AssessmentDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetArchivedBilansListQueryHandler : IRequestHandler<GetArchivedBilansListQuery, PaginatedList<AssessmentDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetArchivedBilansListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<AssessmentDto>> Handle(GetArchivedBilansListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Assessment.GetArchivedBilans()
                    .ProjectTo<AssessmentDto>(_mapper.ConfigurationProvider)
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
