using AdminAreaManagement.Application.Common.Models;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Queries;

[AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("ReferenceData.View")]

public class GetCitiesListQuery : IRequest<PaginatedList<CityDto>>
{
    public string Filter { get; set; }
    public string OrderBy { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }

    public class GetCitiesListQueryHandler : IRequestHandler<GetCitiesListQuery, PaginatedList<CityDto>>
    {
        private readonly ICityQueries _cities;

        public GetCitiesListQueryHandler(ICityQueries cities) => _cities = cities;

        public Task<PaginatedList<CityDto>> Handle(GetCitiesListQuery request, CancellationToken cancellationToken) =>
            _cities.GetPageAsync(request.Filter, request.OrderBy, request.PageNumber, request.PageSize, cancellationToken);
    }
}
