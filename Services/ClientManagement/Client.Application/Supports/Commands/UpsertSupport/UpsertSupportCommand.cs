using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using FluentValidation.Results;
using MediatR;

namespace ClientManagement.Application.Supports.Commands.UpsertSupport
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.EditAll", "Clients.EditAssigned")]
    public class UpsertSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int SocialWorkerId { get; set; }
        public int ClientId { get; set; }
        public string Note { get; set; }

        public class UpsertSupportCommandHandler : IRequestHandler<UpsertSupportCommand, int>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMediator _mediator;

            public UpsertSupportCommandHandler(
                IRepositoryManager repository,
                IMediator mediator)
            {
                _repository = repository;
                _mediator = mediator;
            }

            public async Task<int> Handle(UpsertSupportCommand request, CancellationToken cancellationToken)
            {
                SocialCase entity;

                if (request.SupportId.HasValue)
                {
                    entity = _repository.Support.Get(request.SupportId.Value);
                    if (entity is null)
                        throw new NotFoundException(nameof(SocialCase), request.SupportId.Value);

                    // An ordinary update cannot transfer a case or reassign its worker. Check the
                    // persisted resource before validating dates or changing any tracked fields.
                    if (entity.ClientId != request.ClientId || entity.SocialWorkerId != request.SocialWorkerId)
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("Identity", "Support identity cannot be changed by an ordinary update.")
                        });

                    if (await _repository.Support.DateIsEarlierThanExistingDates(
                            entity.ClientId, request.StartDate, entity.Id))
                        throw new ValidationException(new[]
                        {
                            new ValidationFailure("Property", "Action impossible, la date doit être postérieure au dernier suivi !")
                        });

                    entity.StartDate = request.StartDate.ToLocalTime();
                    entity.EndDate = request.EndDate;
                    entity.Note = request.Note;
                }
                else
                {
                    var Client = await _repository.Client.GetAsync(request.ClientId, true);
                    if(Client == null)
                    {
                        throw new NotFoundException(nameof(Client), request.ClientId);
                    }

                    var SocialWorker = _repository.SocialWorker.Get(request.SocialWorkerId);
                    if (SocialWorker == null)
                    {
                        throw new NotFoundException(nameof(entity), request.SupportId);
                    }

                    entity = new SocialCase(Client,request.StartDate, SocialWorker, request.Note);
                }

                _repository.Support.Persist(entity);
                await _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
