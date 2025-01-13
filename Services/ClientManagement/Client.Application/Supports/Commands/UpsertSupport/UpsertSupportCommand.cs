using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Commands.UpsertSupport
{
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
                Support entity;

                if (request.SupportId.HasValue)
                {
                    DateTime start = request.StartDate.ToLocalTime();

                    entity = _repository.Support.Get(request.SupportId.Value);
                    entity.StartDate = start;
                    entity.EndDate = request.EndDate;
                    entity.ClientId = request.SocialWorkerId;
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

                    entity = new Support(Client,request.StartDate, SocialWorker, request.Note);
                }

                _repository.Support.Persist(entity);
                _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
