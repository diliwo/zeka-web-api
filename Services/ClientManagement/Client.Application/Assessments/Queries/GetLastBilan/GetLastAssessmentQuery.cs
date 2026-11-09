using ClientManagement.Application.Assessments.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Assessments.Queries.GetLastBilan
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetLastAssessmentQuery : IRequest<AssessmentDto>
    {
        public int ClientId { get; set; }

        public class GetLastAssessmentHandler : IRequestHandler<GetLastAssessmentQuery, AssessmentDto>
        {
            private readonly IRepositoryManager _repository;

            public GetLastAssessmentHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<AssessmentDto> Handle(GetLastAssessmentQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Assessment.GetAssessments(request.ClientId)
                    .Where(b => b.Softdelete != true && b.IsFinalized == true)
                    .OrderBy(b => b.Id)
                    .AsNoTracking()
                    .ProjectAssessmentDtos()
                    .LastOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
