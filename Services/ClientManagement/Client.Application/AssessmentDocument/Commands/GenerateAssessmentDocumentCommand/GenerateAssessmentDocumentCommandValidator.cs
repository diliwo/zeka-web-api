using ClientManagement.Core.Interfaces;
using FluentValidation;

namespace ClientManagement.Application.AssessmentDocument.Commands.GenerateAssessmentDocumentCommand
{
    public class GenerateAssessmentDocumentCommandValidator : AbstractValidator<GenerateAssessmentDocumentCommand>
    {
        private readonly IRepositoryManager _repository;

        public GenerateAssessmentDocumentCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.AssessmentId)
                .NotEmpty().WithMessage("AssessmentId is required.");
        }
    }
}
