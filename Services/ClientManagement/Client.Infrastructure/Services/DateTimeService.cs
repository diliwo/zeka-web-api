using ClientManagement.Core.Interfaces;

namespace ClientManagement.Infrastructure.Services
{
    public class DateTimeService : IDateTime
    {
        public DateTime Now => DateTime.Now;
    }
}
