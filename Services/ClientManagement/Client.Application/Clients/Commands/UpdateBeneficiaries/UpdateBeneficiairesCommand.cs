using Client.Application.Clients.Commands.Exceptions;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Clients.Commands.UpdateBeneficiaries
{
    public class UpdateBeneficiairesCommand : IRequest<int>
    {
        //public int Id { get; set; }
        public List<string> ListOfNiss { get; set; }

        public class UpdateBeneficiairesCommandHandler : IRequestHandler<UpdateBeneficiairesCommand, int>
        {
            public readonly IRepositoryManager _repository;
            public readonly IClientService _ClientService;

            public UpdateBeneficiairesCommandHandler(
                IRepositoryManager repository,
                IClientService ClientService
                )
            {
                _repository = repository;
                _ClientService = ClientService;
            }

            public async Task<int> Handle(UpdateBeneficiairesCommand request, CancellationToken cancellationToken)
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
