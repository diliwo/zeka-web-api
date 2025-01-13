using ClientManagement.Application.Clients.Commands.Exceptions;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Clients.Commands.UpdateClient
{
    public class UpdateClientCommand : IRequest<int>
    {
        //public int Id { get; set; }
        public List<string> ListOfNiss { get; set; }

        public class UpdateClientCommandHandler : IRequestHandler<UpdateClientCommand, int>
        {
            public readonly IRepositoryManager _repository;

            public UpdateClientCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpdateClientCommand request, CancellationToken cancellationToken)
            {
                if (request.ListOfNiss.Count == 0)
                {
                    throw new ClientBadRequestException();
                }

                return 0;
            }
        }
    }
}
