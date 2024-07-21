using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Application.QuarterlyMonitorings.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary
{
    public class GetQuarterlyMonitoringsByBeneficiaryQuery : IRequest<PaginatedList<QuarterlyMonitoringDto>>
    {
        public int BeneficiaryId { get; set; }
        public string Filter { get; set; } = "";
        public bool WithDeleted { get; set; } = false;
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 10;
    }

    public class GetQuarterlyMonitoringsByBeneficiaryQueryHandler : IRequestHandler<GetQuarterlyMonitoringsByBeneficiaryQuery, PaginatedList<QuarterlyMonitoringDto>>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMapper _mapper;
        public GetQuarterlyMonitoringsByBeneficiaryQueryHandler(IRepositoryManager repository, IMapper mapper)
        {
            _repository = repository;
            _mapper = mapper;
        }
        public async Task<PaginatedList<QuarterlyMonitoringDto>> Handle(GetQuarterlyMonitoringsByBeneficiaryQuery query, CancellationToken cancellationToken)
        {
            var qMonitorings = await _repository.QuarterlyMonitoring.getQuarterlyMonitoringsByBeneficiaryId(query.BeneficiaryId, query.Filter, query.WithDeleted)
                .Include(q => q.Beneficiary)
                .Include(q => q.Referent)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.Referent.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);
            return qMonitorings;
        }
    }


}
