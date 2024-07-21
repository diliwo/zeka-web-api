using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Bilans.Commands.FinalizeBilan
{
    public class FinalizeBilanCommand : IRequest<int>
    {
        public int? BilanId { get; set; }

        public class FinalizeBilanCommandHandler : IRequestHandler<FinalizeBilanCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public FinalizeBilanCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(FinalizeBilanCommand request, CancellationToken cancellationToken)
            {
                Bilan entity;

                if (!request.BilanId.HasValue)
                {
                    throw new InvalidOperationException(nameof(Bilan));
                }
               
                entity = _repository.Bilan.GetBilanById(request.BilanId.Value);

                if (entity.IsFinalized == false)
                {
                    entity.IsFinalized = true;
                }

                _repository.Bilan.Persist(entity);

                return entity.Id;
            }
        }
    }
}
