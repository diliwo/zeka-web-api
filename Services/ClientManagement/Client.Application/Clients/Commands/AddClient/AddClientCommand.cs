using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Clients.Commands.AddClient
{
    public class AddClientCommand : IRequest<long>
    {
        //public int Id { get; set; }
        public string Niss { get; set; }

        public class AddClientCommandHandler : IRequestHandler<AddClientCommand, long>
        {
            public readonly IRepositoryManager _repository;
            public readonly IClientService _ClientService;

            public AddClientCommandHandler(
                IRepositoryManager repository,
                IClientService ClientService
                )
            {
                _repository = repository;
                _ClientService = ClientService;
            }

            public async Task<long> Handle(AddClientCommand request, CancellationToken cancellationToken)
            {
                var id = await _ClientService.UpSert(request.Niss);

                return id;

            }
        }
    }
}
