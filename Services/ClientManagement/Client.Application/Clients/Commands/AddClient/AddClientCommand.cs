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

            public AddClientCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<long> Handle(AddClientCommand request, CancellationToken cancellationToken)
            {
                return 0;

            }
        }
    }
}
