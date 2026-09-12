using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Supports.Commands.CloseTrack
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.EditAll", "Clients.EditAssigned")]
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
                SocialCase entity;

                if (!request.SupportId.HasValue)
                {
                    throw new InvalidOperationException(nameof(SocialCase));
                }

                entity = _repository.Support.Get(request.SupportId.Value);

                entity.EndDate = request.EndDate.ToLocalTime();
                entity.ReasonOfClosure = request.ReasonOfClosure;

                _repository.Support.Persist(entity);

                await _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
