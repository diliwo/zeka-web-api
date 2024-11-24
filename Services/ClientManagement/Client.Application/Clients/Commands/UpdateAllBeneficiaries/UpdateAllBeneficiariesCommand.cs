using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Clients.Commands.UpdateAllClients
{
    public class UpdateAllClientsCommand : IRequest<int>
    {
        public class UpdateAllClientsCommandHandler : IRequestHandler<UpdateAllClientsCommand, int>
        {
            public readonly IRepositoryManager _repository;
            public readonly IClientService _ClientService;

            public UpdateAllClientsCommandHandler(
                IRepositoryManager repository,
                IClientService ClientService
                )
            {
                _repository = repository;
                _ClientService = ClientService;
            }

            public async Task<int> Handle(UpdateAllClientsCommand request, CancellationToken cancellationToken)
            {
                var nisses = await _repository.Client.GetClientNissesAsync(false);

                var numberOfUpdatedClients = await _ClientService.Update(nisses);

                return numberOfUpdatedClients;
            }
        }
    }
}
