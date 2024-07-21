namespace DiliBeneficiary.Application.Supports.Commands.SendReferentChangedNotification.Messaging.Config
{
    public class RabbitMqExchangeSettings
    {
        public bool SyncIspReferent { get; set; }
        public string ProducerExchangeName { get; set; }
        public string MessageType { get; set; }
        public string ReferentChangedMessageRoutingKey { get; set; }
        public string AnnualClosureTiksExchangeName { get; set; }
        public string AnnualClosureQueueAndRoutingKey { get; set; }
    }
}
