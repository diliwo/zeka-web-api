﻿using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Application.QuarterlyMonitorings.Common;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary
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
            var qMonitorings = await _repository.MonitoringReport.getQuarterlyMonitoringsByClientId(query.ClientId, query.Filter, query.WithDeleted)
                .Include(q => q.Client)
                .Include(q => q.SocialWorker)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.StaffMember.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);
            return qMonitorings;
        }
    }


}
