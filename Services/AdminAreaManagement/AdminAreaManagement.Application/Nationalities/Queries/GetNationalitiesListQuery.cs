using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Nationalities.Queries
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("ReferenceData.View")]
    public class GetNationalitiesListQuery : IRequest<PaginatedList<NationalityDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetNationalitiesListQueryHandler : IRequestHandler<GetNationalitiesListQuery, PaginatedList<NationalityDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<NationalityDto> _sortCities;
            private readonly IMapper _mapper;

            public GetNationalitiesListQueryHandler(
                IRepositoryManager repository,
                ISortHelper<NationalityDto> sortCities,
             IMapper mapper)
            {
                _repository = repository;
                _sortCities = sortCities;
                _mapper = mapper;
            }

            public async Task<PaginatedList<NationalityDto>> Handle(GetNationalitiesListQuery request, CancellationToken cancellationToken)
            {
                var cities = _sortCities.ApplySort(_repository.Nationality.GetNationalities(request.Filter)
                    .ProjectTo<NationalityDto>(_mapper.ConfigurationProvider), request.OrderBy);

                return await cities.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}
