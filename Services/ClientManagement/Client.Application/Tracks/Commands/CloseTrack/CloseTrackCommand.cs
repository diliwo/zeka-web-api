using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Tracks.Commands.CloseTrack
{
    public class CloseTrackCommand : IRequest<int>
    {
        public int? TrackId { get; set; }
        public string ReasonOfClosure { get; set; }
        public DateTime EndDate { get; set; }

        public class CloseSupportCommandHandler : IRequestHandler<CloseTrackCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public CloseSupportCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(CloseTrackCommand request, CancellationToken cancellationToken)
            {
                Track entity;

                if (!request.TrackId.HasValue)
                {
                    throw new InvalidOperationException(nameof(Track));
                }
               
                entity = _repository.Track.Get(request.TrackId.Value);

                entity.EndDate = request.EndDate.ToLocalTime();
                entity.ReasonOfClosure = request.ReasonOfClosure;

                _repository.Track.Persist(entity);

                _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
