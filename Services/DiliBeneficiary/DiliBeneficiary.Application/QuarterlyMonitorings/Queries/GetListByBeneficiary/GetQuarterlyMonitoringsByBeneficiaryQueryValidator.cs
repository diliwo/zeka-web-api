using FluentValidation;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary
{
    public class GetQuarterlyMonitoringsByBeneficiaryQueryValidator : AbstractValidator<GetQuarterlyMonitoringsByBeneficiaryQuery>
    {
        public GetQuarterlyMonitoringsByBeneficiaryQueryValidator()
        {
            RuleFor(x => x.PageNumber)
                .GreaterThanOrEqualTo(1).WithMessage("PageNumber doit être supérieur ou égal à 1");
            RuleFor(x => x.PageSize)
                .GreaterThanOrEqualTo(1).WithMessage("PageSize doit être supérieur ou égal à 1");
        }
    }
}
