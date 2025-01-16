using ClientManagement.Application.Clients.Commands.Exceptions;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using MediatR;
using Language = ClientManagement.Core.ValueObjects.Language;

namespace ClientManagement.Application.Clients.Commands.AddClient
{
    public class AddClientCommand : IRequest<int>
    {
        public string ReferenceNumber { get; set; }
        public CivilStatus CivilStatus { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public Gender Gender { get; set; }
        public DateTime BirthDate { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string Nationality { get; set; }
        public string Ssn { get; set; }
        public Email Email { get; set; }
        public Phone Phone { get; set; }
        public Phone MobilePhone { get; set; }
        public bool HasChildren { get; set; }
        public Language NativeLanguage { get; set; }
        public Language ContactLanguage { get; set; }
        public Address Address { get; set; }
        public int SocialWorkerId { get; set; }

        public class AddClientCommandHandler : IRequestHandler<AddClientCommand, int>
        {
            public readonly IRepositoryManager _repository;

            public AddClientCommandHandler(
                IRepositoryManager repository
            )
            {
                _repository = repository;
            }

            public async Task<int> Handle(AddClientCommand request, CancellationToken cancellationToken)
            {
                var foundedClient = _repository.Client.GetClientBySsN(request.Ssn);

                if (foundedClient is not null)
                {
                    throw new ClientBadRequestException("Client already exist!");
                }

                //var client = new Client(
                //    request.ReferenceNumber,
                //    request.CivilStatus,
                //    request.FirstName,
                //    request.LastName,
                //    request.Gender,
                //    request.BirthDate,
                //    request.Nationality,
                //    request.Ssn,
                //    request.Email,
                //    request.Phone,
                //    request.MobilePhone,
                //    request.NativeLanguage,
                //    request.ContactLanguage,
                //    request.Address,
                //    request.)

                return 1;
            }
        }
    }
}
