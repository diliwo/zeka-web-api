using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.MonitoringActions.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.MonitoringActions.Queries.GetAll
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("ReferenceData.View")]
    public class GetMonitoringActionsQuery : IRequest<List<MonitoringActionDto>>
    {
        public GetMonitoringActionsQuery() { }
    }

    public class GetMonitoringActionsQueryHandler : IRequestHandler<GetMonitoringActionsQuery, List<MonitoringActionDto>>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;

        public GetMonitoringActionsQueryHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public async Task<List<MonitoringActionDto>> Handle(GetMonitoringActionsQuery request, CancellationToken cancellationToken)
        {
            return await _monitoringActionRepository.getMonitoringActions()
                .ProjectActions()
                .ToListAsync(cancellationToken);
        }
    }
}
