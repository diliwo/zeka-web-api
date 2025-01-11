using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Tracks.Queries
{
    public class GetTracksListQuery : IRequest<TracksListVm>
    {
        public class GetTracksListQueryHandler : IRequestHandler<GetTracksListQuery, TracksListVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetTracksListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<TracksListVm> Handle(GetTracksListQuery request, CancellationToken cancellationToken)
            {
                var supports = await _repository.Track.GetTracks()
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .ToListAsync(cancellationToken);

                var vm = new TracksListVm
                {
                    Tracks = supports
                };

                return vm;
            }
        }
    }
}
