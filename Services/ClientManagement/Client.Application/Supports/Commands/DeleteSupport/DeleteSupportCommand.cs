using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Supports.Commands.SendReferentChangedNotification;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Commands.DeleteSupport
{
    public class DeleteSupportCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteSupportCommandHandler : IRequestHandler<DeleteSupportCommand>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMediator _mediator;

            public DeleteSupportCommandHandler(IRepositoryManager repository, IMediator mediator)
            {
                _repository = repository;
                _mediator = mediator;
            }

            public async Task Handle(DeleteSupportCommand request, CancellationToken cancellationToken)
            {
                var foundedSupport = _repository.Support.Get(request.Id);

                if (foundedSupport != null)
                {
                    var clientId = foundedSupport.ClientId;

                    _repository.Support.SoftDelete(foundedSupport);

                    // We get the previous track
                    var previousSupport = await _repository.Support.GetLastSupportForClientAsync(foundedSupport.ClientId);

                    if (previousSupport is not null && previousSupport.Id != foundedSupport.Id)
                    {
                        await _mediator.Publish(new SendSocialWorkerChangedNotificationCommand(previousSupport.Id),
                            cancellationToken);
                    }

                }
                else
                {
                    throw new NotFoundException(nameof(Support), request.Id);
                }
            }
        }
    }
}
