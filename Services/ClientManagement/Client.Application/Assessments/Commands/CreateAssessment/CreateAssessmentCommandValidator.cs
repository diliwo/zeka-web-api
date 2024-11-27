﻿using Client.Core.Interfaces;
using FluentValidation;

namespace Client.Application.Assessments.Commands.CreateAssessment
{
    public class CreateAssessmentCommandValidator : AbstractValidator<CreateAssessmentCommand>
    {
        private readonly IRepositoryManager _repository;

        public CreateAssessmentCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.ClientId)
                .NotEmpty().WithMessage("ClientId is required.")
                .MustAsync(async (obj, id,cancellationToken) =>
                {
                    bool result = await _repository.Assessment.AreAllBilansNotFinalized(obj.ClientId);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, an assessment is not  finalized ");
        }
    }
}