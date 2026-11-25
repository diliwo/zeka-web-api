using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Application.SchoolRegistations.Common;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.SchoolRegistations.Queries.GetSchoolList
{
    [ClientManagement.Application.Common.Authorization.RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
    public class GetSchoolRegistrationsListQuery : IRequest<PaginatedList<SchoolRegistrationDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; }
        public string Orderby { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetSchoolRegistrationsListQueryHandler : IRequestHandler<GetSchoolRegistrationsListQuery, PaginatedList<SchoolRegistrationDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<SchoolRegistrationDto> _sort;

            public GetSchoolRegistrationsListQueryHandler(IRepositoryManager repository, ISortHelper<SchoolRegistrationDto> sort)
            {
                _repository = repository;
                _sort = sort;
            }

            public async Task<PaginatedList<SchoolRegistrationDto>> Handle(GetSchoolRegistrationsListQuery request, CancellationToken cancellationToken)
            {
                if (request.ClientId == null)
                {
                    throw new NotFoundException("Client",request.ClientId);
                }

                var resgistrations = _sort.ApplySort(
                    _repository.SchoolRegistration
                        .GetResgistrationsByClientId(request.ClientId, request.Filter)
                        .Cast<SchoolRegistration>()
                        .Select(registration => new SchoolRegistrationDto
                        {
                            SchoolRegistrationId = registration.Id,
                            TrainingId = registration.TrainingId,
                            TrainingName = registration.Training.Name,
                            SchoolId = registration.SchoolId,
                            SchoolName = registration.School.Name,
                            TrainingTypeId = registration.TrainingTypeId,
                            TrainingTypeName = registration.TrainingType.Name,
                            ClientId = registration.ClientId,
                            BenficiaryName = FormatClientName(registration.Client.LastName, registration.Client.FirstName),
                            StartDate = registration.StartDate,
                            EnDate = registration.EnDate,
                            CourseLevel = registration.CourseLevel,
                            Result = registration.Result,
                            Note = registration.Note
                        }), request.Orderby);

                return await resgistrations.PaginatedListAsync(request.PageNumber, request.PageSize);
            }

            // The legacy display name is final-projection formatting. It was not a translatable sort key.
            private static string FormatClientName(string lastName, string firstName) => $"{lastName} {firstName}";
        }
    }
}
