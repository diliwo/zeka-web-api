using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Application.QuarterlyMonitorings.Common;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.QuarterlyMonitorings.Queries.GetListByReferent
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetQuarterlyMonitoringsByStaffMemberQuery : IRequest<PaginatedList<QuarterlyMonitoringDto>>
    {
        public int StaffMemberId { get; set; }
        public string Filter { get; set; } = "";
        public bool WithDeleted { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

    public class GetQuarterlyMonitoringsByStaffMemberQueryHandler : IRequestHandler<GetQuarterlyMonitoringsByStaffMemberQuery,
        PaginatedList<QuarterlyMonitoringDto>>
    {
        private readonly IRepositoryManager _repository;

        public GetQuarterlyMonitoringsByStaffMemberQueryHandler(IRepositoryManager repository)
        {
            _repository = repository;
        }

        public async Task<PaginatedList<QuarterlyMonitoringDto>> Handle(GetQuarterlyMonitoringsByStaffMemberQuery query, CancellationToken cancellationToken)
        {
            var qMonitorings = await _repository.MonitoringReport.getQuarterlyMonitoringsByStaffMemberId(query.StaffMemberId, query.Filter, query.WithDeleted)
                .OrderByDescending(x => x.ActionDate)
                .ProjectQuarterly()
                .PaginatedListAsync(query.PageNumber, query.PageSize);
            return qMonitorings;
        }
    }
}
