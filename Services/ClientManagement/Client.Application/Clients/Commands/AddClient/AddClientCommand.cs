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
        public string PlaceOfBirth { get; set; }
        public string Nationality { get; set; }
        public string Ssn { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string MobilePhone { get; set; }
        public string NativeLanguage { get; set; }
        public string ContactLanguage { get; set; }
        public Address Address { get; set; }
        public string SocialWorker { get; set; }

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
                var client = new Client(
                    request.ReferenceNumber,
                    request.CivilStatus,
                    request.FirstName,
                    request.LastName,
                    request.Gender,
                    request.BirthDate,
                    request.PlaceOfBirth,
                    request.Nationality,
                    request.Ssn,
                    new Email(request.Email),
                    new Phone(request.Phone),
                    new Phone(request.MobilePhone),
                    new Language(request.NativeLanguage),
                    new Language(request.ContactLanguage),
                    request.Address,
                    request.SocialWorker);

                _repository.Client.Persist(client);

                _repository.Save();

                return client.Id;
            }
        }
    }
}
