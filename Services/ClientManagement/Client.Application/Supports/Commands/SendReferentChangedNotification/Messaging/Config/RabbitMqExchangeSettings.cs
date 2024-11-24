namespace Client.Application.Supports.Commands.SendStaffChangedNotification.Messaging.Config
{
    public class RabbitMqExchangeSettings
    {
        public bool SyncIspStaff { get; set; }
        public string ProducerExchangeName { get; set; }
        public string MessageType { get; set; }
        public string StaffChangedMessageRoutingKey { get; set; }
        public string AnnualClosureTiksExchangeName { get; set; }
        public string AnnualClosureQueueAndRoutingKey { get; set; }
    }
}
