using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Teams.Queries
{
    public class GetTeamsListQuery : IRequest<PaginatedList<TeamDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetServicesListQueryHandler : IRequestHandler<GetTeamsListQuery, PaginatedList<TeamDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<TeamDto> _sortServices;
            private readonly IMapper _mapper;

            public GetServicesListQueryHandler(
                IRepositoryManager repository,
                ISortHelper<TeamDto> sortServices,
             IMapper mapper)
            {
                _repository = repository;
                _sortServices = sortServices;
                _mapper = mapper;
            }

            public async Task<PaginatedList<TeamDto>> Handle(GetTeamsListQuery request, CancellationToken cancellationToken)
            {
                var services = _sortServices.ApplySort(_repository.Team.GetTeams(request.Filter)
                    .ProjectTo<TeamDto>(_mapper.ConfigurationProvider), request.OrderBy);

                return await services.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}
