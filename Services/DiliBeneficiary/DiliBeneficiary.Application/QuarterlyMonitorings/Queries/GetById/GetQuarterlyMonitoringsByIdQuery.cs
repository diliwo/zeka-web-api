using AutoMapper;
using DiliBeneficiary.Application.QuarterlyMonitorings.Common;
using MediatR;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetById
{
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
        private readonly IMapper _mapper;

        public GetQuarterlyMonitoringsByIdQueryHandler(IRepositoryManager repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }

        public async Task<QuarterlyMonitoringDto> Handle(GetQuarterlyMonitoringsByIdQuery query, CancellationToken cancellationToken)
        {
            var qMonitoring =  await _repository.QuarterlyMonitoring.GetQuarterlyMonitoringById(query.Id)
                .Include(q => q.Beneficiary)
                .Include(q => q.Referent)
                .Include(q => q.MonitoringAction)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .SingleOrDefaultAsync(cancellationToken);
            return qMonitoring;
        }
    }
}
