using AutoMapper;
using DiliBeneficiary.Application.QuarterlyMonitorings.Common;
using MediatR;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetListByReferent
{
    public class GetQuarterlyMonitoringsByReferentQuery : IRequest<PaginatedList<QuarterlyMonitoringDto>>
    {
        public int ReferentId { get; set; }
        public string Filter { get; set; } = "";
        public bool WithDeleted { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

    public class GetQuarterlyMonitoringsByReferentQueryHandler : IRequestHandler<GetQuarterlyMonitoringsByReferentQuery,
        PaginatedList<QuarterlyMonitoringDto>>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMapper _mapper;
        public GetQuarterlyMonitoringsByReferentQueryHandler(IRepositoryManager repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }

        public async Task<PaginatedList<QuarterlyMonitoringDto>> Handle(GetQuarterlyMonitoringsByReferentQuery query, CancellationToken cancellationToken)
        {
            var qMonitorings = await _repository.QuarterlyMonitoring.getQuarterlyMonitoringsByReferentId(query.ReferentId, query.Filter, query.WithDeleted)
                .Include(q => q.Beneficiary)
                .Include(q => q.Referent)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.Beneficiary.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);
            return qMonitorings;
        }
    }
}
