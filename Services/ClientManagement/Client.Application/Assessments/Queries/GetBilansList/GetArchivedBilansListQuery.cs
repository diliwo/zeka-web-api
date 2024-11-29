using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Assessments.Common;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Assessments.Queries.GetBilansList
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
