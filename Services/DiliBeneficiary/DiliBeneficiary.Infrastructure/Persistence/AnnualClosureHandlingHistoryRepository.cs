using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class AnnualClosureHandlingHistoryRepository : RepositoryBase<AnnualClosureHandlingHistory>, IAnnualClosureHandlingHistoryRepository
    {
        public AnnualClosureHandlingHistoryRepository(ApplicationDbContext context) : base(context)
        {
        }

        public bool Exists(DateTime closureDate, string niss)
        {
            return ApplicationDbContext.AnnualClosuresHandlingHistories.Any(h =>
                h.ClosureStartDate == closureDate && h.Niss == niss);
        }

        public void AddHandling(DateTime closureDate, string niss, string referntUserName)
        {
            ApplicationDbContext.AnnualClosuresHandlingHistories.Add(new AnnualClosureHandlingHistory
            {
                ClosureStartDate = closureDate,
                Niss = niss,
                ReferentUserName = referntUserName,
                HandlingDate = DateTime.Now
            });
            ApplicationDbContext.SaveChanges();
        }
    }
}
