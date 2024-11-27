using MediatR;

namespace Client.Application.Tracks.Commands.SendReferentChangedNotification
{
    public class SendStaffChangedNotificationCommand : INotification
    {
        public SendStaffChangedNotificationCommand(int trackId)
        {
            TrackId = trackId;
        }

        public SendStaffChangedNotificationCommand()
        {
        }

        public int TrackId { get; set; }
    }
}
