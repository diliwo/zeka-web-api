﻿using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Application.QuarterlyMonitorings.Common;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.QuarterlyMonitorings.Queries.GetList
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
                .Include(q => q.Client)
                .Include(q => q.Staff)
                .Include(q => q.MonitoringAction)
                .OrderByDescending(x => x.ActionDate)
                //.ThenBy(q => q.Staff.FullName)
                //.ThenBy(q => q.Client.FullName)
                .ProjectTo<QuarterlyMonitoringDto>(_mapper.ConfigurationProvider)
                .PaginatedListAsync(query.PageNumber, query.PageSize);

            return qMonitorings;
        }
    }
}