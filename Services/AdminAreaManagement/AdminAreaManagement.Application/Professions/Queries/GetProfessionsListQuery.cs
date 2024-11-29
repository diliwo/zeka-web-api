using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Professions.Queries
{
    public class GetProfessionsListQuery : IRequest<PaginatedList<ProfessionDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetProfessionsListQueryHandler : IRequestHandler<GetProfessionsListQuery, PaginatedList<ProfessionDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly ISortHelper<ProfessionDto> _sort;
            private readonly IMapper _mapper;

            public GetProfessionsListQueryHandler(IRepositoryManager repository, ISortHelper<ProfessionDto> sort, IMapper mapper)
            {
                _repository = repository;
                _sort = sort;
                _mapper = mapper;
            }

            public async Task<PaginatedList<ProfessionDto>> Handle(GetProfessionsListQuery request, CancellationToken cancellationToken)
            {
                var services = _sort.ApplySort(_repository.Profession.GetProfessions(request.Filter, request.OrderBy)
                    .ProjectTo<ProfessionDto>(_mapper.ConfigurationProvider), request.OrderBy);

                return await services.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}
