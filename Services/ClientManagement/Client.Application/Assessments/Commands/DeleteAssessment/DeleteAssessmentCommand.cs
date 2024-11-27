﻿using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Assessments.Commands.DeleteAssessment
{
    public class DeleteAssessmentCommand : IRequest<Unit>
    {
        public int BilanId { get; set; }

        public class DeleteAssessmentCommandHandler : IRequestHandler<DeleteAssessmentCommand, Unit>
        {
            private readonly IRepositoryManager _repository;

            public DeleteAssessmentCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteAssessmentCommand request, CancellationToken cancellationToken)
            {
                var entity = _repository.Assessment.GetBilanById(request.BilanId);

                if (entity != null)
                {
                    _repository.Assessment.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(Training), request.BilanId);
                }

                return Unit.Value;
            }
        }

    }
}