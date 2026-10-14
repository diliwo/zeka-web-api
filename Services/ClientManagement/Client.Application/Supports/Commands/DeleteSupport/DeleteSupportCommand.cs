using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Supports.Commands.SendReferentChangedNotification;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Commands.DeleteSupport
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.Delete")]
    public class DeleteSupportCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteSupportCommandHandler : IRequestHandler<DeleteSupportCommand>
        {
            private readonly IRepositoryManager _repository;
            private readonly PostCommitNotifications _notifications;
            private readonly IClientParentLock _locks;

            public DeleteSupportCommandHandler(IRepositoryManager repository, PostCommitNotifications notifications,
                IClientParentLock locks)
            {
                _repository = repository;
                _notifications = notifications;
                _locks = locks;
            }

            public async Task Handle(DeleteSupportCommand request, CancellationToken cancellationToken)
            {
                var clientId = await _locks.ResolveCaseClientAsync(request.Id, cancellationToken);
                if (clientId is null) throw new NotFoundException(nameof(SocialCase), request.Id);
                await _locks.LockAsync(clientId.Value, cancellationToken);
                var foundedSupport = _repository.Support.Get(request.Id);

                if (foundedSupport != null)
                {
                    _repository.Support.SoftDelete(foundedSupport);

                    // We get the previous track
                    var previousSupport = _repository.Support.GetSupports()
                        .Where(x => x.ClientId == foundedSupport.ClientId && x.Id != foundedSupport.Id)
                        .OrderByDescending(x => x.StartDate).FirstOrDefault();

                    if (previousSupport is not null && previousSupport.Id != foundedSupport.Id)
                    {
                        _notifications.Enqueue(new SendSocialWorkerChangedNotificationCommand(previousSupport.Id));
                    }

                }
                else
                {
                    throw new NotFoundException(nameof(SocialCase), request.Id);
                }
            }
        }
    }
}
