using ClientManagement.Core.Interfaces;
using FluentValidation;

namespace ClientManagement.Application.Assessments.Commands.FinalizeAssessment
{
    public class FinalizeBilanCommandValidator : AbstractValidator<FinalizeAssessmentCommand>
    {

        private readonly IRepositoryManager _repository;

        public FinalizeBilanCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BilanId)
                .MustAsync(async (obj, date, cancellationToken) =>
                {
                    bool result = await _repository.Assessment.IsBilanNotFinalizd((int)obj.BilanId);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, the assessment is already completed !");
        }
    }
}
