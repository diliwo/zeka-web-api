using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Tracks.Commands.SendReferentChangedNotification;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Tracks.Commands.DeleteSupport
{
    public class DeleteTrackCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteTrackCommandHandler : IRequestHandler<DeleteTrackCommand>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMediator _mediator;

            public DeleteTrackCommandHandler(IRepositoryManager repository, IMediator mediator)
            {
                _repository = repository;
                _mediator = mediator;
            }

            public async Task Handle(DeleteTrackCommand request, CancellationToken cancellationToken)
            {
                var foundedSupport = _repository.Track.Get(request.Id);

                if (foundedSupport != null)
                {
                    var benefiaryId = foundedSupport.ClientId;

                    _repository.Track.SoftDelete(foundedSupport);

                    // We get the previous track
                    var previousSupport = await _repository.Track.GetLastTrackForClientAsync(foundedSupport.ClientId);

                    if (previousSupport is not null && previousSupport.Id != foundedSupport.Id)
                    {
                        await _mediator.Publish(new SendStaffMemberChangedNotificationCommand(previousSupport.Id),
                            cancellationToken);
                    }

                }
                else
                {
                    throw new NotFoundException(nameof(Track), request.Id);
                }
            }
        }
    }
}
