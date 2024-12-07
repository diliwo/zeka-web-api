using AdminAreaManagement.Application.Common.Mappings;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Formations.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;

namespace AdminAreaManagement.Application.Formations.Queries.GetTrainingList
{
    public class GetTrainingListQuery : IRequest<PaginatedList<TrainingDto>>
    {
        public string Filter { get; set; }
        public string OrderBy { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetTrainingListQueryHandler : IRequestHandler<GetTrainingListQuery, PaginatedList<TrainingDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<TrainingDto> _sortFormation;
            private readonly IMapper _mapper;

            public GetTrainingListQueryHandler(
                IRepositoryManager repository,
                ISortHelper<TrainingDto> sortFormation,
                IMapper mapper)
            {
                _repository = repository;
                _sortFormation = sortFormation;
                _mapper = mapper;
            }

            public async Task<PaginatedList<TrainingDto>> Handle(GetTrainingListQuery request, CancellationToken cancellationToken)
            {
                var trainings = _sortFormation.ApplySort(_repository.Trainings
                    .GetTrainings(request.Filter)
                    .ProjectTo<TrainingDto>(_mapper.ConfigurationProvider), request.OrderBy);

               return await trainings.PaginatedListAsync(request.PageNumber, request.PageSize);
            }
        }
    }
}