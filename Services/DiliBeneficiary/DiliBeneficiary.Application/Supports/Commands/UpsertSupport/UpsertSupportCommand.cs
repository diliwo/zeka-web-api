using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Commands.UpsertSupport
{
    public class UpsertSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int ReferentId { get; set; }
        public int BeneficiaryId { get; set; }
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
                    entity.ReferentId = request.ReferentId;
                    entity.Note = request.Note;
                }
                else
                {
                    var beneficiary = await _repository.Beneficiary.GetAsync(request.BeneficiaryId, true);
                    if(beneficiary == null)
                    {
                        throw new NotFoundException(nameof(beneficiary), request.BeneficiaryId);
                    }

                    var referent = _repository.Referent.Get(request.ReferentId);
                    if (referent == null)
                    {
                        throw new NotFoundException(nameof(entity), request.SupportId);
                    }

                    entity = new Support(beneficiary,request.StartDate, referent, request.Note);
                }

                _repository.Support.Persist(entity);
                _repository.SaveAsync();

                await _mediator.Publish(new SendReferentChangedNotificationCommand(entity.Id),
                    cancellationToken);

                return entity.Id;
            }
        }
    }
}
