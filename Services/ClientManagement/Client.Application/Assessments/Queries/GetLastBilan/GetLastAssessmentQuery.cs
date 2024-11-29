using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Assessments.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Assessments.Queries.GetLastBilan
{
    public class GetLastAssessmentQuery : IRequest<AssessmentDto>
    {
        public int ClientId { get; set; }

        public class GetLastAssessmentHandler : IRequestHandler<GetLastAssessmentQuery, AssessmentDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetLastAssessmentHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<AssessmentDto> Handle(GetLastAssessmentQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Assessment.GetAssessments(request.ClientId)
                    .Where(b => b.Softdelete != true && b.IsFinalized == true)
                    .OrderBy(b => b.Id)
                    .AsNoTracking()
                    .ProjectTo<AssessmentDto>(_mapper.ConfigurationProvider)
                    .LastOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
