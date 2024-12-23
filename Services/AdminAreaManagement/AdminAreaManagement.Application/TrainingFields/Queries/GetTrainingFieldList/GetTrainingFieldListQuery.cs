using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.TrainingFields.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.TrainingFields.Queries.GetTrainingFieldList
{
    public class GetTrainingFieldListQuery : IRequest<PaginatedList<TrainingFieldDto>>
    {
        public string Filter { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public string OrderBy { get; set; }

        public class GetTrainingFieldListQueryHandler : IRequestHandler<GetTrainingFieldListQuery, PaginatedList<TrainingFieldDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<TrainingFieldDto> _sort;
            private readonly IMapper _mapper;

            public GetTrainingFieldListQueryHandler(IRepositoryManager repository, IMapper mapper, ISortHelper<TrainingFieldDto> sort)
            {
                _repository = repository;
                _sort = sort;
                _mapper = mapper;
            }

            public async Task<PaginatedList<TrainingFieldDto>> Handle(GetTrainingFieldListQuery request, CancellationToken cancellationToken)
            {
                var fields = _sort.ApplySort(_repository.TrainingField.GetFields(request.Filter)
                    .ProjectTo<TrainingFieldDto>(_mapper.ConfigurationProvider), request.OrderBy);

                return await fields.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}