using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.QuarterlyMonitorings.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.QuarterlyMonitorings.Queries.GetById
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetQuarterlyMonitoringsByIdQuery : IRequest<QuarterlyMonitoringDto>
    {
        public int Id { get; set; }

        public GetQuarterlyMonitoringsByIdQuery(int id)
        {
            Id = id;
        }
    }

    public class GetQuarterlyMonitoringsByIdQueryHandler : IRequestHandler<GetQuarterlyMonitoringsByIdQuery, QuarterlyMonitoringDto>
    {
        private readonly IRepositoryManager _repository;


        public GetQuarterlyMonitoringsByIdQueryHandler(IRepositoryManager repository)
        {
            _repository = repository;
        }

        public async Task<QuarterlyMonitoringDto> Handle(GetQuarterlyMonitoringsByIdQuery query, CancellationToken cancellationToken)
        {
            var qMonitoring =  await _repository.MonitoringReport.GetQuarterlyMonitoringById(query.Id)
                .ProjectQuarterly()
                .SingleOrDefaultAsync(cancellationToken);
            return qMonitoring;
        }
    }
}
