using Client.Application.Common.Exceptions;
using Client.Application.Tracks.Commands.SendReferentChangedNotification;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Tracks.Commands.UpsertSupport
{
    public class UpsertSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int StaffId { get; set; }
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
                Track entity;

                if (request.SupportId.HasValue)
                {
                    DateTime start = request.StartDate.ToLocalTime();

                    entity = _repository.Track.Get(request.SupportId.Value);
                    entity.StartDate = start;
                    entity.EndDate = request.EndDate;
                    entity.StaffId = request.StaffId;
                    entity.Note = request.Note;
                }
                else
                {
                    var Client = await _repository.Client.GetAsync(request.ClientId, true);
                    if(Client == null)
                    {
                        throw new NotFoundException(nameof(Client), request.ClientId);
                    }

                    var Staff = _repository.Staff.Get(request.StaffId);
                    if (Staff == null)
                    {
                        throw new NotFoundException(nameof(entity), request.SupportId);
                    }

                    entity = new Track(Client,request.StartDate, Staff, request.Note);
                }

                _repository.Track.Persist(entity);
                _repository.SaveAsync();

                await _mediator.Publish(new SendStaffChangedNotificationCommand(entity.Id),
                    cancellationToken);

                return entity.Id;
            }
        }
    }
}
