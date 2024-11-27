using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Application.QuarterlyMonitorings.Common;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary
{
    public class GetQuarterlyMonitoringsByClientQuery : IRequest<PaginatedList<QuarterlyMonitoringDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; } = "";
        public bool WithDeleted { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

    public class GetQuarterlyMonitoringsByClientQueryHandler : IRequestHandler<GetQuarterlyMonitoringsByClientQuery, PaginatedList<QuarterlyMonitoringDto>>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMapper _mapper;
        public GetQuarterlyMonitoringsByClientQueryHandler(IRepositoryManager repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }
        public async Task<PaginatedList<QuarterlyMonitoringDto>> Handle(GetQuarterlyMonitoringsByClientQuery query, CancellationToken cancellationToken)
        {
            var qMonitorings = await _repository.QuarterlyMonitoring.getQuarterlyMonitoringsByClientId(query.ClientId, query.Filter, query.WithDeleted)
                .Include(q => q.Client)
                .Include(q => q.Staff)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.Staff.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);
            return qMonitorings;
        }
    }


}
