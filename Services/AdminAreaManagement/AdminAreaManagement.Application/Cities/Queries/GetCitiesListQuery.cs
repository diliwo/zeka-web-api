using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Queries
{
    public class GetCitiesListQuery : IRequest<PaginatedList<CityDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetCitiesListQueryHandler : IRequestHandler<GetCitiesListQuery, PaginatedList<CityDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<CityDto> _sortCities;
            private readonly IMapper _mapper;

            public GetCitiesListQueryHandler(
                IRepositoryManager repository,
                ISortHelper<CityDto> sortCities,
             IMapper mapper)
            {
                _repository = repository;
                _sortCities = sortCities;
                _mapper = mapper;
            }

            public async Task<PaginatedList<CityDto>> Handle(GetCitiesListQuery request, CancellationToken cancellationToken)
            {
                var cities = _sortCities.ApplySort(_repository.City.GetCities(request.Filter)
                    .ProjectTo<CityDto>(_mapper.ConfigurationProvider), request.OrderBy);

                return await cities.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}
