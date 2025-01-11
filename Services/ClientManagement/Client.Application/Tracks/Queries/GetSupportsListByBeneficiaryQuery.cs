using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Tracks.Queries
{
    public class GetTracksListByClientQuery : IRequest<PaginatedList<SupportDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; }
        public string SortOrder { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }


        public class GetTracksListByClientQueryHandler : IRequestHandler<GetTracksListByClientQuery, PaginatedList<SupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetTracksListByClientQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<SupportDto>> Handle(GetTracksListByClientQuery request, CancellationToken cancellationToken)
            {
                var supports = await _repository.Track.GetTracksByClientId(request.ClientId)
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);

                return supports;
            }
        }
    }
}
