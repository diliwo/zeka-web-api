using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.TrainingTypes.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.TrainingTypes.Queries.GetTrainingTypeList
{
    public class GetTrainingTypeListQuery : IRequest<PaginatedList<TrainingTypeDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public string OrderBy { get; set; }

        public class GetTrainingTypeListQueryHandler : IRequestHandler<GetTrainingTypeListQuery, PaginatedList<TrainingTypeDto>>
        {
            private readonly IRepositoryManager _repository;
             private ISortHelper<TrainingTypeDto> _sort;
            private readonly IMapper _mapper;

            public GetTrainingTypeListQueryHandler(IRepositoryManager repository, ISortHelper<TrainingTypeDto> sort,IMapper mapper)
            {
                _repository = repository;
                _sort = sort;
                _mapper = mapper;
            }

            public async Task<PaginatedList<TrainingTypeDto>> Handle(GetTrainingTypeListQuery request, CancellationToken cancellationToken)
            {
                 var types = _sort.ApplySort(_repository.TrainingType.GetTypes(request.Filter, request.OrderBy)
                    .ProjectTo<TrainingTypeDto>(_mapper.ConfigurationProvider),request.OrderBy);

                 return await types.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}