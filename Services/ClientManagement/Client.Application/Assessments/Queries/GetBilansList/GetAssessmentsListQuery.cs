using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Assessments.Common;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Assessments.Queries.GetBilansList
{
    public class GetAssessmentsListQuery : IRequest<PaginatedList<AssessmentDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int ClientId { get; set; }

        public class GetBilansListQueryHandler : IRequestHandler<GetAssessmentsListQuery, PaginatedList<AssessmentDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBilansListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<AssessmentDto>> Handle(GetAssessmentsListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Assessment.GetAssessments(request.ClientId)
                    .Include(i => i.BilanProfessions)
                    .ProjectTo<AssessmentDto>(_mapper.ConfigurationProvider)
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
