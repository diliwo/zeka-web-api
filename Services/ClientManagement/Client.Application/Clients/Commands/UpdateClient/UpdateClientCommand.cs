using ClientManagement.Application.Clients.Commands.Exceptions;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Clients.Commands.UpdateBeneficiaries
{
    public class UpdateClientCommand : IRequest<int>
    {
        //public int Id { get; set; }
        public List<string> ListOfNiss { get; set; }

        public class UpdateClientCommandHandler : IRequestHandler<UpdateClientCommand, int>
        {
            public readonly IRepositoryManager _repository;
            public readonly IClientService _ClientService;

            public UpdateClientCommandHandler(
                IRepositoryManager repository,
                IClientService ClientService
                )
            {
                _repository = repository;
                _ClientService = ClientService;
            }

            public async Task<int> Handle(UpdateClientCommand request, CancellationToken cancellationToken)
            {
                if (request.ListOfNiss.Count == 0)
                {
                    throw new ClientBadRequestException();
                }

                var numberOfUpdatedClients = await _ClientService.Update(request.ListOfNiss);

                return numberOfUpdatedClients;
            }
        }
    }
}
