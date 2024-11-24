using MediatR;

namespace Client.Application.Supports.Commands.SendStaffChangedNotification
{
    public class SendStaffChangedNotificationCommand : INotification
    {
        public SendStaffChangedNotificationCommand(int supportId)
        {
            SupportId = supportId;
        }

        public SendStaffChangedNotificationCommand()
        {
        }

        public int SupportId { get; set; }
    }

    //public class SendStaffChangedNotificationCommandHandler : INotificationHandler<SendStaffChangedNotificationCommand>
    //{
    //    private readonly IRepositoryManager _repository;
    //    private readonly IQueueService _producingService;
    //    private readonly RabbitMqExchangeSettings _rabbitConfig;

    //    public SendStaffChangedNotificationCommandHandler(IRepositoryManager repository, IQueueService producingService, RabbitMqExchangeSettings rabbitConfig)
    //    {
    //        _repository = repository;
    //        _producingService = producingService;
    //        _rabbitConfig = rabbitConfig;
    //    }

    //    public async Task Handle(SendStaffChangedNotificationCommand notificationCommand, CancellationToken cancellationToken)
    //    {
    //        var support = _repository.Track.GetWithDetails(notificationCommand.SupportId);
    //        if (support == null)
    //        {
    //            throw new NotFoundException(nameof(Track), notificationCommand.SupportId);
    //        }

    //        var message = new IspStaffChangedMessage
    //        {
    //            WorkerFirstName = support.Staff.FirstName,
    //            WorkerLastName = support.Staff.LastName,
    //            WorkerSamAccountName = support.Staff.UserName ,
    //            ClientNiss = support.Client.Niss,
    //            ClientFirstName = support.Client.FirstName,
    //            ClientLastName = support.Client.LastName,
    //            ClientReferenceNumber = support.Client.ReferenceNumber
    //        };
    //        await _producingService.SendAsync(message, _rabbitConfig.ProducerExchangeName, _rabbitConfig.StaffChangedMessageRoutingKey);
    //    }
    //}
}
