using FluentValidation;

namespace Client.Application.QuarterlyMonitorings.Queries.GetListByStaff
{
    public class GetQuarterlyMonitoringsByStaffQueryValidator :AbstractValidator<GetQuarterlyMonitoringsByStaffQuery>
    {
        public GetQuarterlyMonitoringsByStaffQueryValidator()
        {
            RuleFor(x => x.PageNumber)
                .GreaterThanOrEqualTo(1).WithMessage("PageNumber doit être supérieur ou égal à 1");
            RuleFor(x => x.PageSize)
                .GreaterThanOrEqualTo(1).WithMessage("PageSize doit être supérieur ou égal à 1");
        }
    }
}
