using MediatR;

namespace ClientManagement.Application.Supports.Commands.SendReferentChangedNotification
{
    public class SendSocialWorkerChangedNotificationCommand : INotification
    {
        public SendSocialWorkerChangedNotificationCommand(int trackId)
        {
            TrackId = trackId;
        }

        public SendSocialWorkerChangedNotificationCommand()
        {
        }

        public int TrackId { get; set; }
    }
}
