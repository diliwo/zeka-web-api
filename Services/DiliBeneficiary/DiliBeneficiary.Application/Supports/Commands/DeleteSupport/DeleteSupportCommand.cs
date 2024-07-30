using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Commands.DeleteSupport
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
                    var benefiaryId = foundedSupport.BeneficiaryId;

                    _repository.Support.SoftDelete(foundedSupport);

                    // We get the previous support
                    var previousSupport = await _repository.Support.GetLastSupportForBeneficiaryAsync(foundedSupport.BeneficiaryId);

                    if (previousSupport is not null && previousSupport.Id != foundedSupport.Id)
                    {
                        await _mediator.Publish(new SendReferentChangedNotificationCommand(previousSupport.Id),
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
