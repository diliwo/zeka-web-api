using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Assessments.Commands.FinalizeAssessment
{
    public class FinalizeAssessmentCommand : IRequest<int>
    {
        public int? BilanId { get; set; }

        public class FinalizeAssessmentCommandHandler : IRequestHandler<FinalizeAssessmentCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public FinalizeAssessmentCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(FinalizeAssessmentCommand request, CancellationToken cancellationToken)
            {
                Assessment entity;

                if (!request.BilanId.HasValue)
                {
                    throw new InvalidOperationException(nameof(Assessment));
                }
               
                entity = _repository.Assessment.GetBilanById(request.BilanId.Value);

                if (entity.IsFinalized == false)
                {
                    entity.IsFinalized = true;
                }

                _repository.Assessment.Persist(entity);

                return entity.Id;
            }
        }
    }
}
