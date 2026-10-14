using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Exceptions;
using FluentValidation.Results;

namespace ClientManagement.Application.Supports.Commands.CloseTrack
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.EditAll", "Clients.EditAssigned")]
    public class CloseSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public string ReasonOfClosure { get; set; }
        public DateTime EndDate { get; set; }

        public class CloseSupportCommandHandler : IRequestHandler<CloseSupportCommand, int>
        {
            private readonly IRepositoryManager _repository;
            private readonly IClientParentLock _locks;

            public CloseSupportCommandHandler(
                IRepositoryManager repository, IClientParentLock locks
            )
            {
                _repository = repository;
                _locks = locks;
            }

            public async Task<int> Handle(CloseSupportCommand request, CancellationToken cancellationToken)
            {
                SocialCase entity;

                if (!request.SupportId.HasValue)
                {
                    throw new InvalidOperationException(nameof(SocialCase));
                }

                var clientId = await _locks.ResolveCaseClientAsync(request.SupportId.Value, cancellationToken);
                if (clientId is null) throw new NotFoundException(nameof(SocialCase), request.SupportId.Value);
                await _locks.LockAsync(clientId.Value, cancellationToken);
                entity = _repository.Support.Get(request.SupportId.Value)
                    ?? throw new NotFoundException(nameof(SocialCase), request.SupportId.Value);
                if (!await _repository.Support.EndDateIsGreaterThanStartDate(entity.Id, request.EndDate))
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure("Property", "Action not allowed, The end date is not consistent !")
                    });

                entity.EndDate = request.EndDate.ToLocalTime();
                entity.ReasonOfClosure = request.ReasonOfClosure;

                _repository.Support.Persist(entity);

                await _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
