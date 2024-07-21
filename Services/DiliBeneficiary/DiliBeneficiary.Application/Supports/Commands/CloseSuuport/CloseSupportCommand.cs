using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Commands.CloseSuuport
{
    public class CloseSupportCommand : IRequest<int>
    {
        public int? SupportId { get; set; }
        public string ReasonOfClosure { get; set; }
        public DateTime EndDate { get; set; }

        public class CloseSupportCommandHandler : IRequestHandler<CloseSupportCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public CloseSupportCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(CloseSupportCommand request, CancellationToken cancellationToken)
            {
                Support entity;

                if (!request.SupportId.HasValue)
                {
                    throw new InvalidOperationException(nameof(Support));
                }
               
                entity = _repository.Support.Get(request.SupportId.Value);

                entity.EndDate = request.EndDate.ToLocalTime();
                entity.ReasonOfClosure = request.ReasonOfClosure;

                _repository.Support.Persist(entity);

                _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
