using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Infrastructure.Services
{
    public class DateTimeService : IDateTime
    {
        public DateTime Now => DateTime.Now;
    }
}
