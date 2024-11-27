//using Client.Core.Interfaces;
//using FluentValidation;

//namespace Client.Application.BilanDocument.Commands.GenerateBilanDocumentCommand
//{
//    public class GenerateBilanDocumentCommandValidator : AbstractValidator<GenerateBilanDocumentCommand>
//    {
//        private readonly IRepositoryManager _repository;

//        public GenerateBilanDocumentCommandValidator(IRepositoryManager repository)
//        {
//            _repository = repository;

//            RuleFor(v => v.AssessmentId)
//                .NotEmpty().WithMessage("AssessmentId is required.");
//        }
//    }
//}
