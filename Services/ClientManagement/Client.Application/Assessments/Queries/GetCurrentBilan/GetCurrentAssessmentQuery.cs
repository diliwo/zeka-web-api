using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Assessments.Common;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Assessments.Queries.GetCurrentBilan
{
    public class GetCurrentAssessmentQuery : IRequest<AssessmentDto>
    {
        public int ClientId { get; set; }

        public class GetClientDetailQueryHandler : IRequestHandler<GetCurrentAssessmentQuery, AssessmentDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<AssessmentDto> Handle(GetCurrentAssessmentQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Assessment.GetAssessments(request.ClientId)
                    .Where(b => b.IsFinalized != true)
                    .AsNoTracking()
                    .ProjectTo<AssessmentDto>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);

                return vm;
            }
        }
    }
}
