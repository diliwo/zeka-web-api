using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.MonitoringActions.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.MonitoringActions.Queries.GetById
{
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
        private readonly IMapper _mapper;
        public GetMonitoringActionByIdHandler(IMonitoringActionRepository monitoringActionRepository, IMapper mapper)
        {
            _monitoringActionRepository = monitoringActionRepository;
            _mapper = mapper;
        }

        public async Task<MonitoringActionDto> Handle(GetMonitoringActionById request, CancellationToken cancellationToken)
        {
            return await _monitoringActionRepository.GetMonitoringActionById(request.ActionId)
                .ProjectTo<MonitoringActionDto>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync(cancellationToken);
        }
    }
}
