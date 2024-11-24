using Client.Application.Common.Exceptions;
using Client.Application.Supports.Commands.SendStaffChangedNotification;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Supports.Commands.DeleteSupport
{
    public class DeleteTrackCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteSupportCommandHandler : IRequestHandler<DeleteTrackCommand>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMediator _mediator;

            public DeleteSupportCommandHandler(IRepositoryManager repository, IMediator mediator)
            {
                _repository = repository;
                _mediator = mediator;
            }

            public async Task Handle(DeleteTrackCommand request, CancellationToken cancellationToken)
            {
                var foundedSupport = _repository.Support.Get(request.Id);

                if (foundedSupport != null)
                {
                    var benefiaryId = foundedSupport.ClientId;

                    _repository.Support.SoftDelete(foundedSupport);

                    // We get the previous support
                    var previousSupport = await _repository.Support.GetLastSupportForClientAsync(foundedSupport.ClientId);

                    if (previousSupport is not null && previousSupport.Id != foundedSupport.Id)
                    {
                        await _mediator.Publish(new SendStaffChangedNotificationCommand(previousSupport.Id),
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
