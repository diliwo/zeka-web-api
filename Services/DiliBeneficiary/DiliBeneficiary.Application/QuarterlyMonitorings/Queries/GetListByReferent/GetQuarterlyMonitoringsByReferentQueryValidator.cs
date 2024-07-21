using FluentValidation;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetListByReferent
{
    public class GetQuarterlyMonitoringsByReferentQueryValidator :AbstractValidator<GetQuarterlyMonitoringsByReferentQuery>
    {
        public GetQuarterlyMonitoringsByReferentQueryValidator()
        {
            RuleFor(x => x.PageNumber)
                .GreaterThanOrEqualTo(1).WithMessage("PageNumber doit être supérieur ou égal à 1");
            RuleFor(x => x.PageSize)
                .GreaterThanOrEqualTo(1).WithMessage("PageSize doit être supérieur ou égal à 1");
        }
    }
}
