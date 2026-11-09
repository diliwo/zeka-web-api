using ClientManagement.Application.Assessments.Common;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Assessments.Queries.GetBilansList
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetAssessmentsListQuery : IRequest<PaginatedList<AssessmentDto>>
    {
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int ClientId { get; set; }

        public class GetBilansListQueryHandler : IRequestHandler<GetAssessmentsListQuery, PaginatedList<AssessmentDto>>
        {
            private readonly IRepositoryManager _repository;

            public GetBilansListQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<PaginatedList<AssessmentDto>> Handle(GetAssessmentsListQuery request, CancellationToken cancellationToken)
            {
                return await _repository.Assessment.GetAssessments(request.ClientId)
                    .Include(i => i.BilanProfessions)
                    .ProjectAssessmentDtos()
                    .OrderByDescending(s => s.BilanId)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
