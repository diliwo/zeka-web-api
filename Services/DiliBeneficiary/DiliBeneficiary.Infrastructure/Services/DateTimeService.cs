using DiliBeneficiary.Core.Interfaces;

namespace DiliBeneficiary.Infrastructure.Services
{
    public class DateTimeService : IDateTime
    {
        public DateTime Now => DateTime.Now;
    }
}
