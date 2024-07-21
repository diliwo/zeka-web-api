using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.MonitoringActions.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.MonitoringActions.Queries.GetAll
{
    public class GetMonitoringActionsQuery : IRequest<List<MonitoringActionDto>>
    {
        public GetMonitoringActionsQuery() { }
    }

    public class GetMonitoringActionsQueryHandler : IRequestHandler<GetMonitoringActionsQuery, List<MonitoringActionDto>>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        private readonly IMapper _mapper;
        public GetMonitoringActionsQueryHandler(IMonitoringActionRepository monitoringActionRepository, IMapper mapper)
        {
            _monitoringActionRepository = monitoringActionRepository;
            _mapper = mapper;
        }

        public async Task<List<MonitoringActionDto>> Handle(GetMonitoringActionsQuery request, CancellationToken cancellationToken)
        {
            return await _monitoringActionRepository.getMonitoringActions()
                .ProjectTo<MonitoringActionDto>(_mapper.ConfigurationProvider)
                .ToListAsync(cancellationToken);
        }
    }
}
