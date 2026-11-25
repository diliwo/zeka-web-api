using ClientManagement.Application.Assessments.Common;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Assessments.Queries.GetBilansList
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetArchivedBilansListQuery : IRequest<PaginatedList<AssessmentDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetArchivedBilansListQueryHandler : IRequestHandler<GetArchivedBilansListQuery, PaginatedList<AssessmentDto>>
        {
            private readonly IRepositoryManager _repository;

            public GetArchivedBilansListQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<PaginatedList<AssessmentDto>> Handle(GetArchivedBilansListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Assessment.GetArchivedBilans()
                    .ProjectAssessmentDtos()
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
