using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.MonitoringActions.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.MonitoringActions.Queries.GetById
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("ReferenceData.View")]
    public class GetMonitoringActionById : IRequest<MonitoringActionDto>
    {
        public int ActionId { get; set; }
        public GetMonitoringActionById(int id)
        {
            ActionId = id;
        }
    }

    public class GetMonitoringActionByIdHandler : IRequestHandler<GetMonitoringActionById, MonitoringActionDto>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;

        public GetMonitoringActionByIdHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public async Task<MonitoringActionDto> Handle(GetMonitoringActionById request, CancellationToken cancellationToken)
        {
            return await _monitoringActionRepository.GetMonitoringActionById(request.ActionId)
                .ProjectActions()
                .SingleOrDefaultAsync(cancellationToken);
        }
    }
}
