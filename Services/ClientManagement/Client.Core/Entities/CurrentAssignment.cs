using System.Linq.Expressions;

namespace ClientManagement.Core.Entities;

public static class CurrentAssignment
{
    // Calendar dates: start inclusive, end exclusive. A future end does not end today's assignment.
    public static Expression<Func<SocialCase, bool>> ActiveOn(DateTime date) =>
        support => !support.Softdelete && support.StartDate <= date
            && (support.EndDate == null || support.EndDate > date);
}
