using AutoMapper;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Application.QuarterlyMonitorings.Common;
using MediatR;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetList
{
    public class GetQuarterlyMonitoringsQuery : IRequest<PaginatedList<QuarterlyMonitoringDto>>
    {
        public string Filter { get; set; } = "";
        public bool WithDeleted { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

    public class GetQuarterlyMonitoringsQueryHandler : IRequestHandler<GetQuarterlyMonitoringsQuery, PaginatedList<QuarterlyMonitoringDto>>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMapper _mapper;
        public GetQuarterlyMonitoringsQueryHandler(IRepositoryManager repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }
        public async Task<PaginatedList<QuarterlyMonitoringDto>> Handle(GetQuarterlyMonitoringsQuery query, CancellationToken cancellationToken)
        {
            var qMonitorings = await _repository.QuarterlyMonitoring.getQuarterlyMonitorings(query.Filter,query.WithDeleted)
                .Include(q => q.Beneficiary)
                .Include(q => q.Referent)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.Referent.FullName)
                //.ThenBy(q => q.Beneficiary.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);

            return qMonitorings;
        }
    }
}
