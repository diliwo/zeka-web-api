using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Assessments.Commands.DeleteAssessment
{
    public class DeleteAssessmentCommand : IRequest<Unit>
    {
        public int AssessmentId { get; set; }

        public class DeleteAssessmentCommandHandler : IRequestHandler<DeleteAssessmentCommand, Unit>
        {
            private readonly IRepositoryManager _repository;

            public DeleteAssessmentCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteAssessmentCommand request, CancellationToken cancellationToken)
            {
                var entity = _repository.Assessment.GetAssessmentById(request.AssessmentId);

                if (entity != null)
                {
                    _repository.Assessment.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(Training), request.AssessmentId);
                }

                return Unit.Value;
            }
        }

    }
}