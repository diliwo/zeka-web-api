using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Tracks.Commands.SendReferentChangedNotification;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Tracks.Commands.UpsertSupport
{
    public class UpsertSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int StaffMemberId { get; set; }
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

                    entity = _repository.Track.Get(request.SupportId.Value);
                    entity.StartDate = start;
                    entity.EndDate = request.EndDate;
                    entity.StaffMemberId = request.StaffMemberId;
                    entity.Note = request.Note;
                }
                else
                {
                    var Client = await _repository.Client.GetAsync(request.ClientId, true);
                    if(Client == null)
                    {
                        throw new NotFoundException(nameof(Client), request.ClientId);
                    }

                    var StaffMember = _repository.StaffMember.Get(request.StaffMemberId);
                    if (StaffMember == null)
                    {
                        throw new NotFoundException(nameof(entity), request.SupportId);
                    }

                    entity = new Support(Client,request.StartDate, StaffMember, request.Note);
                }

                _repository.Track.Persist(entity);
                _repository.SaveAsync();

                await _mediator.Publish(new SendStaffMemberChangedNotificationCommand(entity.Id),
                    cancellationToken);

                return entity.Id;
            }
        }
    }
}
