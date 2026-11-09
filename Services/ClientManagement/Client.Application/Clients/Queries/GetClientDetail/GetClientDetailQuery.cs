using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Clients.Queries.GetClientDetail
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetClientDetailQuery : IRequest<ClientDto>
    {
        public int ClientId { get; set; }

        public class GetClientDetailQueryHandler : IRequestHandler<GetClientDetailQuery, ClientDto>
        {
            private readonly IRepositoryManager _repository;

            public GetClientDetailQueryHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<ClientDto> Handle(GetClientDetailQuery request, CancellationToken cancellationToken)
            {
                var clients = _repository.Client.GetClients()
                    .Where(client => client.Id == request.ClientId)
                    .AsNoTracking();

                var details =
                    from client in clients
                    let addressNumber = EF.Property<string?>(client, "AddressNumber")
                    let addressStreet = EF.Property<string?>(client, "AddressStreet")
                    let addressPostalCode = EF.Property<string?>(client, "AddressPostalCode")
                    let addressCity = EF.Property<string?>(client, "AddressCity")
                    let addressCountry = EF.Property<string?>(client, "AddressCountry")
                    let supports = client.SocialCases.Where(support => !support.Softdelete)
                    let trainings = client.SchoolRegistrations.Where(training => !training.Softdelete)
                    select new ClientDto
                    {
                        ClientId = client.Id,
                        ReferenceNumber = client.ReferenceNumber!,
                        CivilStatus = (int)client.CivilStatus,
                        FirstName = client.FirstName,
                        LastName = client.LastName,
                        Gender = (int)client.Gender,
                        BirthDate = client.BirthDate,
                        PlaceOfBirth = client.PlaceOfBirth,
                        Nationality = client.Nationality,
                        Email = client.Email!.EmailAddress,
                        Phone = client.Phone!.PhoneNumber,
                        MobilePhone = client.MobilePhone!.PhoneNumber,
                        SupportStaffMemberName = supports.Any()
                            ? supports.OrderBy(support => support.Created)
                                .Select(support => support.SocialWorker.LastName + " " + support.SocialWorker.FirstName)
                                .Last()
                            : string.Empty,
                        SupportStaffMemberService = supports.Any()
                            ? supports.OrderBy(support => support.Created)
                                .Select(support => "(" + support.SocialWorker.TeamAcronym + ")")
                                .Last()
                            : string.Empty,
                        SupportStartDate = supports.OrderBy(support => support.Created).First().StartDate,
                        SupportEndDate = supports.OrderBy(support => support.Created).Last().EndDate,
                        NativeLanguage = client.NativeLanguage!.SpokenLanguage,
                        ContactLanguage = client.ContactLanguage!.SpokenLanguage,
                        Address = addressNumber == null && addressStreet == null && addressPostalCode == null
                            && addressCity == null && addressCountry == null
                            ? null!
                            : new Address(addressNumber!, addressStreet!, addressPostalCode!, addressCity!, addressCountry!),
                        SocialWorkerName = client.SocialWorkerName,
                        LastTrainingName = trainings.OrderBy(training => training.Created).Last().Training.Name,
                        LastTrainingNote = trainings.OrderBy(training => training.Created).Last().Note,
                        LastTrainingResult = (int)trainings.OrderBy(training => training.Created).Last().Result,
                        LastTrainingStartDate = trainings.OrderBy(training => training.Created).Last().StartDate,
                        LastTrainingEndDate = trainings.OrderBy(training => training.Created).Last().EnDate
                    };

                return await details.SingleOrDefaultAsync(cancellationToken);
            }
        }
    }
}
