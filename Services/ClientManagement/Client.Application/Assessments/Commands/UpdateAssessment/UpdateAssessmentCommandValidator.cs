﻿using ClientManagement.Core.Interfaces;
using FluentValidation;

namespace ClientManagement.Application.Assessments.Commands.UpdateAssessment
{
    public class UpdateAssessmentCommandValidator : AbstractValidator<UpdateAssessmentCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpdateAssessmentCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.BilanId)
                .NotEmpty().WithMessage("Id is required.");
        }
    }
}
