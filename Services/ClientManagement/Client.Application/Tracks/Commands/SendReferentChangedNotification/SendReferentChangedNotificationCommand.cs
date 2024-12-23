using MediatR;

namespace ClientManagement.Application.Tracks.Commands.SendReferentChangedNotification
{
    public class SendStaffMemberChangedNotificationCommand : INotification
    {
        public SendStaffMemberChangedNotificationCommand(int trackId)
        {
            TrackId = trackId;
        }

        public SendStaffMemberChangedNotificationCommand()
        {
        }

        public int TrackId { get; set; }
    }
}
