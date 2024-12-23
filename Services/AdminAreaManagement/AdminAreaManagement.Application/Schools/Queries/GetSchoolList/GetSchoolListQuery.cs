using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Schools.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Schools.Queries.GetSchoolList
{
    public class GetSchoolListQuery : IRequest<PaginatedList<SchoolDto>>
    {
        public string Filter { get; set; }
        public string Orderby { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetSchoolListQueryHandler : IRequestHandler<GetSchoolListQuery, PaginatedList<SchoolDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<SchoolDto> _sort;
            private readonly IMapper _mapper;

            public GetSchoolListQueryHandler(
                IRepositoryManager repository,
                IMapper mapper, 
                ISortHelper<SchoolDto> sort)
            {
                _repository = repository;
                _sort = sort;
                _mapper = mapper;
            }

            public async Task<PaginatedList<SchoolDto>> Handle(GetSchoolListQuery request, CancellationToken cancellationToken)
            {
                var schools = 
                        _sort.ApplySort(_repository.School.GetSchools(request.Filter, request.Orderby)
                    .ProjectTo<SchoolDto>(_mapper.ConfigurationProvider), request.Orderby);

                return await schools.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}