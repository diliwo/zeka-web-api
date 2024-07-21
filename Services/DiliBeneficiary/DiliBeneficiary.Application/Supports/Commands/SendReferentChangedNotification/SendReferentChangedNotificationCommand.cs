using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification.Messaging.Config;
using DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification.Messaging.Dto;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification
{
    public class SendReferentChangedNotificationCommand : INotification
    {
        public SendReferentChangedNotificationCommand(int supportId)
        {
            SupportId = supportId;
        }

        public SendReferentChangedNotificationCommand()
        {
        }

        public int SupportId { get; set; }
    }

    //public class SendReferentChangedNotificationCommandHandler : INotificationHandler<SendReferentChangedNotificationCommand>
    //{
    //    private readonly IRepositoryManager _repository;
    //    private readonly IQueueService _producingService;
    //    private readonly RabbitMqExchangeSettings _rabbitConfig;

    //    public SendReferentChangedNotificationCommandHandler(IRepositoryManager repository, IQueueService producingService, RabbitMqExchangeSettings rabbitConfig)
    //    {
    //        _repository = repository;
    //        _producingService = producingService;
    //        _rabbitConfig = rabbitConfig;
    //    }

    //    public async Task Handle(SendReferentChangedNotificationCommand notificationCommand, CancellationToken cancellationToken)
    //    {
    //        var support = _repository.Support.GetWithDetails(notificationCommand.SupportId);
    //        if (support == null)
    //        {
    //            throw new NotFoundException(nameof(Support), notificationCommand.SupportId);
    //        }

    //        var message = new IspReferentChangedMessage
    //        {
    //            WorkerFirstName = support.Referent.FirstName,
    //            WorkerLastName = support.Referent.LastName,
    //            WorkerSamAccountName = support.Referent.UserName ,
    //            BeneficiaryNiss = support.Beneficiary.Niss,
    //            BeneficiaryFirstName = support.Beneficiary.FirstName,
    //            BeneficiaryLastName = support.Beneficiary.LastName,
    //            BeneficiaryReferenceNumber = support.Beneficiary.ReferenceNumber
    //        };
    //        await _producingService.SendAsync(message, _rabbitConfig.ProducerExchangeName, _rabbitConfig.ReferentChangedMessageRoutingKey);
    //    }
    //}
}
