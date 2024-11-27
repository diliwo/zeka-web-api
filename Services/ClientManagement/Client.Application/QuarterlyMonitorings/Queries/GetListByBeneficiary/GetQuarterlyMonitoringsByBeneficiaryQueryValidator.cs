using FluentValidation;

namespace Client.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary
{
    public class GetQuarterlyMonitoringsByClientQueryValidator : AbstractValidator<GetQuarterlyMonitoringsByClientQuery>
    {
        public GetQuarterlyMonitoringsByClientQueryValidator()
        {
            RuleFor(x => x.PageNumber)
                .GreaterThanOrEqualTo(1).WithMessage("PageNumber doit être supérieur ou égal à 1");
            RuleFor(x => x.PageSize)
                .GreaterThanOrEqualTo(1).WithMessage("PageSize doit être supérieur ou égal à 1");
        }
    }
}
