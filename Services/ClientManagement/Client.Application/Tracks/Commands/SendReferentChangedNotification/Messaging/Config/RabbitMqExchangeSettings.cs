namespace ClientManagement.Application.Tracks.Commands.SendReferentChangedNotification.Messaging.Config
{
    public class RabbitMqExchangeSettings
    {
        public bool SyncIspStaffMember { get; set; }
        public string ProducerExchangeName { get; set; }
        public string MessageType { get; set; }
        public string StaffMemberChangedMessageRoutingKey { get; set; }
        public string AnnualClosureTiksExchangeName { get; set; }
        public string AnnualClosureQueueAndRoutingKey { get; set; }
    }
}
