# AdminAreaManagement

AdminAreaManagement is the Zeka backend service for teams, staff, partners and their documents, schools, training, and administrative reference data such as cities, nationalities and professions. It exposes HTTP endpoints and implements a gRPC social-worker lookup.

The files below are grouped by project and then by feature or component. Each entry links to its source file.

[API](#api) · [Application](#application) · [Core](#core) · [Infrastructure](#infrastructure) · [Tests](#tests)

## API

`AdminAreaManagement.API` Hosts the ASP.NET Core endpoints, bearer authentication, tenant request middleware and LocalStack health checks.

### Project files

| File | Purpose |
| --- | --- |
| [`AdminAreaManagement.API.csproj`](AdminAreaManagement.API/AdminAreaManagement.API.csproj) | Defines the .NET 8 web host and its Application, Infrastructure and shared authentication project references. |
| [`AdminAreaManagement.http`](AdminAreaManagement.API/AdminAreaManagement.http) | Contains the ASP.NET starter weather-forecast HTTP request. |
| [`appsettings.Development.json`](AdminAreaManagement.API/appsettings.Development.json) | Supplies local development logging settings. |
| [`appsettings.json`](AdminAreaManagement.API/appsettings.json) | Defines baseline logging, host, tenancy, file-server and RabbitMQ configuration sections. |
| [`DependencyInjection.cs`](AdminAreaManagement.API/DependencyInjection.cs) | Registers web services, JSON handling, Swagger, gRPC, CORS and access to the current HTTP user. |
| [`Dockerfile`](AdminAreaManagement.API/Dockerfile) | Performs the strict locked restore, builds and publishes the API, and packages it in a non-root ASP.NET Core runtime image. |
| [`LocalStackHealth.cs`](AdminAreaManagement.API/LocalStackHealth.cs) | Registers the LocalStack-only `/health/live` and `/health/ready` probes ahead of business authentication. |
| [`packages.lock.json`](AdminAreaManagement.API/packages.lock.json) | Pins the API project's resolved NuGet dependencies and content hashes for locked restore. |
| [`Program.cs`](AdminAreaManagement.API/Program.cs) | Composes the API host, including tenant context, authentication, application and infrastructure services, staff projection transport and the HTTP pipeline. |
| [`TenantAuthentication.cs`](AdminAreaManagement.API/TenantAuthentication.cs) | Registers Zeka's shared bearer-token validation with the API. |

### Controllers

| File | Purpose |
| --- | --- |
| [`ApiControllerBase.cs`](AdminAreaManagement.API/Controllers/ApiControllerBase.cs) | Provides the shared `/api/[controller]` route and MediatR access for controllers. |
| [`CitiesController.cs`](AdminAreaManagement.API/Controllers/CitiesController.cs) | Exposes HTTP operations for listing, creating, updating and deleting city records. |
| [`DocumentsController.cs`](AdminAreaManagement.API/Controllers/DocumentsController.cs) | Lists, imports, retrieves and deletes partner documents, including lookups by partner and job. |
| [`NationalitiesController.cs`](AdminAreaManagement.API/Controllers/NationalitiesController.cs) | Exposes HTTP operations for listing, creating, updating and deleting nationality records. |
| [`PartnersControllers.cs`](AdminAreaManagement.API/Controllers/PartnersControllers.cs) | Exposes partner list, selection, detail, create, update and delete endpoints. |
| [`ProfessionsController.cs`](AdminAreaManagement.API/Controllers/ProfessionsController.cs) | Exposes profession list, upsert and delete endpoints. |
| [`SchoolsController.cs`](AdminAreaManagement.API/Controllers/SchoolsController.cs) | Exposes school list, create, update and delete endpoints. |
| [`StaffController.cs`](AdminAreaManagement.API/Controllers/StaffController.cs) | Exposes staff-member list, upsert and delete operations through HTTP. |
| [`TeamsController.cs`](AdminAreaManagement.API/Controllers/TeamsController.cs) | Exposes team listing, lookup by name, upsert and deletion. |
| [`TrainingFieldController.cs`](AdminAreaManagement.API/Controllers/TrainingFieldController.cs) | Exposes the training-field list endpoint. |
| [`TrainingsController.cs`](AdminAreaManagement.API/Controllers/TrainingsController.cs) | Exposes training list, create, update and delete endpoints. |
| [`TrainingTypeController.cs`](AdminAreaManagement.API/Controllers/TrainingTypeController.cs) | Exposes training-type list, create, update and delete endpoints. |

### Properties

| File | Purpose |
| --- | --- |
| [`launchSettings.json`](AdminAreaManagement.API/Properties/launchSettings.json) | Defines the API's local IDE and development launch profiles. |

### Services

| File | Purpose |
| --- | --- |
| [`AdminareaService.cs`](AdminAreaManagement.API/Services/AdminareaService.cs) | Implements the `GetSocialWorker` gRPC method by dispatching the corresponding application query. |
| [`TenantAccessMiddleware.cs`](AdminAreaManagement.API/Services/TenantAccessMiddleware.cs) | Resolves the recognised HTTP tenant request and maps tenant authority failures to HTTP responses before the application operation proceeds. |
| [`TenantRequestIdentity.cs`](AdminAreaManagement.API/Services/TenantRequestIdentity.cs) | Holds the current request's subject, selected organisation and bearer credential for access checks. |
| [`TenantRequestPolicyAttribute.cs`](AdminAreaManagement.API/Services/TenantRequestPolicyAttribute.cs) | Associates an HTTP endpoint with its application request type and tenant policy. |

## Application

`AdminAreaManagement.Application` Defines administrative commands and queries, validation, application authorization and service contracts. Files are grouped by feature.

### Project files

| File | Purpose |
| --- | --- |
| [`AdminAreaManagement.Application.csproj`](AdminAreaManagement.Application/AdminAreaManagement.Application.csproj) | Defines the .NET 8 application project and its Core, lifecycle, mediation, validation, mapping, and persistence dependencies. |
| [`DependencyInjection.cs`](AdminAreaManagement.Application/DependencyInjection.cs) | Registers application handlers, authorization and validation behaviors, mapping, sorting, file storage and domain-event publication. |
| [`packages.lock.json`](AdminAreaManagement.Application/packages.lock.json) | Pins the resolved NuGet dependencies for the .NET 8 application project. |

### Cities

| File | Purpose |
| --- | --- |
| [`Commands/CreateCity/CreateCityCommand.cs`](AdminAreaManagement.Application/Cities/Commands/CreateCity/CreateCityCommand.cs) | Persists a shared city from its name and country and returns the generated identifier. |
| [`Commands/CreateCity/CreateCityCommandValidator.cs`](AdminAreaManagement.Application/Cities/Commands/CreateCity/CreateCityCommandValidator.cs) | Validates normalized Unicode city and country text and rejects duplicate active name-country pairs. |
| [`Commands/DeleteClity/DeleteCityCommand.cs`](AdminAreaManagement.Application/Cities/Commands/DeleteClity/DeleteCityCommand.cs) | Toggles a city's soft-delete state and checks for an active duplicate before restoring it. |
| [`Commands/DeleteClity/DeleteCityCommandValidator.cs`](AdminAreaManagement.Application/Cities/Commands/DeleteClity/DeleteCityCommandValidator.cs) | Requires a non-empty city identifier for the soft-delete toggle. |
| [`Commands/UpdateCity/UpdateCityCommand.cs`](AdminAreaManagement.Application/Cities/Commands/UpdateCity/UpdateCityCommand.cs) | Updates a city's supplied name and country and returns its identifier. |
| [`Commands/UpdateCity/UpdateCityCommandValidator.cs`](AdminAreaManagement.Application/Cities/Commands/UpdateCity/UpdateCityCommandValidator.cs) | Requires a non-empty city identifier before updating a reference city. |
| [`ICityQueries.cs`](AdminAreaManagement.Application/Cities/ICityQueries.cs) | Defines active city duplicate detection and filtered, sorted, paginated city reads. |
| [`Queries/CitiesDto.cs`](AdminAreaManagement.Application/Cities/Queries/CitiesDto.cs) | Wraps a list of city DTOs in the existing Teams property. |
| [`Queries/CityDto.cs`](AdminAreaManagement.Application/Cities/Queries/CityDto.cs) | Maps a reference city's identifier, name, and country to its read model. |
| [`Queries/GetCitiesListQuery.cs`](AdminAreaManagement.Application/Cities/Queries/GetCitiesListQuery.cs) | Delegates filtering, ordering, and paging to the shared city query interface. |

### Closures

| File | Purpose |
| --- | --- |
| [`AdminAreaClosureContracts.cs`](AdminAreaManagement.Application/Closures/AdminAreaClosureContracts.cs) | Defines AdminArea data and document closure participants and their fence entry and release contracts. |

### Common

| File | Purpose |
| --- | --- |
| [`Authorization/IAdminAreaClosureGate.cs`](AdminAreaManagement.Application/Common/Authorization/IAdminAreaClosureGate.cs) | Defines the durable closure check for ordinary tenant access. |
| [`Authorization/ITenantTransactionExecutor.cs`](AdminAreaManagement.Application/Common/Authorization/ITenantTransactionExecutor.cs) | Defines tenant transaction execution and marks requests that require a non-retried transaction. |
| [`Authorization/TenantAccess.cs`](AdminAreaManagement.Application/Common/Authorization/TenantAccess.cs) | Defines tenant access decisions, permission metadata and operation-scoped authorization that establishes tenant context only after a valid membership grant. |
| [`Authorization/TenantAccessDiagnostics.cs`](AdminAreaManagement.Application/Common/Authorization/TenantAccessDiagnostics.cs) | Validates and filters permission codes and emits bounded logs and activity events for tenant-access integrity failures. |
| [`Behaviours/AuthorizationBehaviour.cs`](AdminAreaManagement.Application/Common/Behaviours/AuthorizationBehaviour.cs) | Authorizes MediatR requests and checks the organisation closure gate inside a tenant transaction, using single-attempt execution for marked requests. |
| [`Behaviours/LoggingBehaviour.cs`](AdminAreaManagement.Application/Common/Behaviours/LoggingBehaviour.cs) | Logs the request type and current user context before MediatR dispatch. |
| [`Behaviours/PerformanceBehaviour.cs`](AdminAreaManagement.Application/Common/Behaviours/PerformanceBehaviour.cs) | Measures request duration and logs requests that exceed 500 milliseconds. |
| [`Behaviours/UnhandledExceptionBehaviour.cs`](AdminAreaManagement.Application/Common/Behaviours/UnhandledExceptionBehaviour.cs) | Logs unexpected handler exceptions and rethrows them, while leaving tenant-specific failures to their dedicated handling. |
| [`Behaviours/ValidationBehaviour.cs`](AdminAreaManagement.Application/Common/Behaviours/ValidationBehaviour.cs) | Runs the request validators and aggregates failures before invoking the handler. |
| [`Exceptions/ApplicationExceptionBase.cs`](AdminAreaManagement.Application/Common/Exceptions/ApplicationExceptionBase.cs) | Defines the base application exception contract for an HTTP status, problem type and title. |
| [`Exceptions/BadRequestException.cs`](AdminAreaManagement.Application/Common/Exceptions/BadRequestException.cs) | Defines the base exception for invalid requests with an explicit message. |
| [`Exceptions/FileAccessException.cs`](AdminAreaManagement.Application/Common/Exceptions/FileAccessException.cs) | Reports document file-access failures with the affected file name and HTTP 405 metadata. |
| [`Exceptions/ForbiddenAccessException.cs`](AdminAreaManagement.Application/Common/Exceptions/ForbiddenAccessException.cs) | Defines the exception used to signal forbidden access. |
| [`Exceptions/ForbiddenOperationException.cs`](AdminAreaManagement.Application/Common/Exceptions/ForbiddenOperationException.cs) | Reports a forbidden candidacy deletion with the candidacy number and HTTP 405 metadata. |
| [`Exceptions/NotFoundException.cs`](AdminAreaManagement.Application/Common/Exceptions/NotFoundException.cs) | Reports missing entities and can include the entity type and lookup key. |
| [`Exceptions/ValidationException.cs`](AdminAreaManagement.Application/Common/Exceptions/ValidationException.cs) | Groups validation failures by property for downstream error handling. |
| [`Helpers/SortHelper.cs`](AdminAreaManagement.Application/Common/Helpers/SortHelper.cs) | Applies comma-separated ordering against recognized DTO properties, resolving names without case sensitivity. |
| [`Mappings/IMapFrom.cs`](AdminAreaManagement.Application/Common/Mappings/IMapFrom.cs) | Defines the AutoMapper convention for DTOs mapped from a source model. |
| [`Mappings/MappingExtensions.cs`](AdminAreaManagement.Application/Common/Mappings/MappingExtensions.cs) | Provides query extensions for AutoMapper projection and asynchronous paginated results. |
| [`Mappings/MappingProfile.cs`](AdminAreaManagement.Application/Common/Mappings/MappingProfile.cs) | Discovers exported mapping types and invokes their AutoMapper registration methods. |
| [`Models/DomainEventNotification.cs`](AdminAreaManagement.Application/Common/Models/DomainEventNotification.cs) | Wraps a domain event as a typed MediatR notification. |
| [`Models/File.cs`](AdminAreaManagement.Application/Common/Models/File.cs) | Carries a file name and the associated bytes. |
| [`Models/PaginatedList.cs`](AdminAreaManagement.Application/Common/Models/PaginatedList.cs) | Counts query results and retrieves the requested page with its paging metadata. |
| [`Models/Result.cs`](AdminAreaManagement.Application/Common/Models/Result.cs) | Represents success or failure with a collection of error messages. |
| [`Protos/adminarea.proto`](AdminAreaManagement.Application/Common/Protos/adminarea.proto) | Defines the gRPC social-worker lookup request and its staff and team response fields. |
| [`Security/AuthorizeAttribute.cs`](AdminAreaManagement.Application/Common/Security/AuthorizeAttribute.cs) | Defines class-level authorization metadata for roles and named policies. |
| [`Services/DomainEventService.cs`](AdminAreaManagement.Application/Common/Services/DomainEventService.cs) | Publishes a domain event as its corresponding typed MediatR notification. |
| [`Services/FileService.cs`](AdminAreaManagement.Application/Common/Services/FileService.cs) | Stores partner files under organisation and document identifiers with path containment, link checks and identical-content reuse. |

### DocumentPartners

| File | Purpose |
| --- | --- |
| [`Commands/Delete/DeleteDocumentPartnerCommand.cs`](AdminAreaManagement.Application/DocumentPartners/Commands/Delete/DeleteDocumentPartnerCommand.cs) | Records an authorized document deletion request using its document and operation identifiers and returns the file-operation state. |
| [`Commands/Persist/PersistDocumentPartnerCommand.cs`](AdminAreaManagement.Application/DocumentPartners/Commands/Persist/PersistDocumentPartnerCommand.cs) | Persists a partner document and hashes the request fields and file bytes to bind the supplied operation identifier to its content. |
| [`DocumentFileOperationResult.cs`](AdminAreaManagement.Application/DocumentPartners/DocumentFileOperationResult.cs) | Exposes the committed document record's operation identifier, file-processing state and failure code for creation or deletion. |
| [`Queries/DocumentPartnerDto.cs`](AdminAreaManagement.Application/DocumentPartners/Queries/DocumentPartnerDto.cs) | Maps persisted partner documents to their identifiers, metadata and file-content fields. |
| [`Queries/GetDocument/GetSelectedDocumentQuery.cs`](AdminAreaManagement.Application/DocumentPartners/Queries/GetDocument/GetSelectedDocumentQuery.cs) | Loads a document by ID, reads its bytes from tenant-scoped storage and distinguishes missing files from other access errors. |
| [`Queries/GetDocumentsList/DocumentPartnersListDto.cs`](AdminAreaManagement.Application/DocumentPartners/Queries/GetDocumentsList/DocumentPartnersListDto.cs) | Wraps the list of partner document DTOs. |
| [`Queries/GetDocumentsList/GetDocumentsJobPartnerListQuery.cs`](AdminAreaManagement.Application/DocumentPartners/Queries/GetDocumentsList/GetDocumentsJobPartnerListQuery.cs) | Projects documents for a specified job and partner in document-ID order. |
| [`Queries/GetDocumentsList/GetDocumentsPartnerListQuery.cs`](AdminAreaManagement.Application/DocumentPartners/Queries/GetDocumentsList/GetDocumentsPartnerListQuery.cs) | Projects the available partner documents in document-ID order. |

### Exceptions

| File | Purpose |
| --- | --- |
| [`FluidException.cs`](AdminAreaManagement.Application/Exceptions/FluidException.cs) | Defines the application exception carrying a message for Fluid-related failures. |

### Exports

| File | Purpose |
| --- | --- |
| [`AdminAreaExportContracts.cs`](AdminAreaManagement.Application/Exports/AdminAreaExportContracts.cs) | Defines participation in organisation export fencing and fragment staging, with separate contracts for verified document artifacts. |

### Nationalities

| File | Purpose |
| --- | --- |
| [`Commands/CreateNationality/CreateNationalityCommand.cs`](AdminAreaManagement.Application/Nationalities/Commands/CreateNationality/CreateNationalityCommand.cs) | Persists a new nationality reference with its supplied name and returns its generated ID. |
| [`Commands/CreateNationality/CreateNationalityCommandValidator.cs`](AdminAreaManagement.Application/Nationalities/Commands/CreateNationality/CreateNationalityCommandValidator.cs) | Checks that a nationality name is present, at most 100 characters and unique among existing names. |
| [`Commands/DeleteNationality/DeleteNationalityCommand.cs`](AdminAreaManagement.Application/Nationalities/Commands/DeleteNationality/DeleteNationalityCommand.cs) | Toggles the soft-deletion flag on a nationality reference and rejects a missing ID. |
| [`Commands/DeleteNationality/DeleteNationalityCommandValidator.cs`](AdminAreaManagement.Application/Nationalities/Commands/DeleteNationality/DeleteNationalityCommandValidator.cs) | Requires a nationality ID for a delete request. |
| [`Commands/UpdateNationality/UpdateNationalityCommand.cs`](AdminAreaManagement.Application/Nationalities/Commands/UpdateNationality/UpdateNationalityCommand.cs) | Accepts a nationality update request but currently resolves and persists the specified ID through the city repository. |
| [`Commands/UpdateNationality/UpdateNationalityCommandValidator.cs`](AdminAreaManagement.Application/Nationalities/Commands/UpdateNationality/UpdateNationalityCommandValidator.cs) | Requires a nationality ID for an update request. |
| [`Queries/GetNationalitiesListQuery.cs`](AdminAreaManagement.Application/Nationalities/Queries/GetNationalitiesListQuery.cs) | Filters, sorts and pages the nationality projection for reference-data lookups. |
| [`Queries/NationalitiesDto.cs`](AdminAreaManagement.Application/Nationalities/Queries/NationalitiesDto.cs) | Wraps a collection of nationality DTOs. |
| [`Queries/NationalityDto.cs`](AdminAreaManagement.Application/Nationalities/Queries/NationalityDto.cs) | Projects a nationality reference to its ID and name. |

### Partners

| File | Purpose |
| --- | --- |
| [`Commands/CreatePartner/CreatePartnerCommand.cs`](AdminAreaManagement.Application/Partners/Commands/CreatePartner/CreatePartnerCommand.cs) | Creates a partner with address, staff contact, classification, agreement dates and contact people. |
| [`Commands/CreatePartner/CreatePartnerCommandValidator.cs`](AdminAreaManagement.Application/Partners/Commands/CreatePartner/CreatePartnerCommandValidator.cs) | Requires the partner address and agreement signature date and limits the note to 255 characters. |
| [`Commands/DeletePartener/DeletePartnerCommand.cs`](AdminAreaManagement.Application/Partners/Commands/DeletePartener/DeletePartnerCommand.cs) | Toggles the partner's soft-deletion flag and saves the change. |
| [`Commands/UpdatePartner/UpdatePartnerCommand.cs`](AdminAreaManagement.Application/Partners/Commands/UpdatePartner/UpdatePartnerCommand.cs) | Updates the partner profile, address, classifications, agreement dates and contact people. |
| [`Commands/UpdatePartner/UpdatePartnerCommandValidator.cs`](AdminAreaManagement.Application/Partners/Commands/UpdatePartner/UpdatePartnerCommandValidator.cs) | Requires the updated partner address fields and limits the note to 255 characters. |
| [`Queries/Common/PartnerDto.cs`](AdminAreaManagement.Application/Partners/Queries/Common/PartnerDto.cs) | Projects partner profile details, contacts, classification names and whether documents exist. |
| [`Queries/Common/PartnerSelectionListDto.cs`](AdminAreaManagement.Application/Partners/Queries/Common/PartnerSelectionListDto.cs) | Projects only the partner ID and name for selection controls. |
| [`Queries/GetPartnerDetail/GetPartnerDetailQuery.cs`](AdminAreaManagement.Application/Partners/Queries/GetPartnerDetail/GetPartnerDetailQuery.cs) | Finds one non-deleted partner by ID and projects its details without entity tracking. |
| [`Queries/GetPartners/GetPartnersListQuery.cs`](AdminAreaManagement.Application/Partners/Queries/GetPartners/GetPartnersListQuery.cs) | Filters, sorts and pages detailed partner projections including their document indicators. |
| [`Queries/GetPartners/PartnerListVm.cs`](AdminAreaManagement.Application/Partners/Queries/GetPartners/PartnerListVm.cs) | Wraps a list of partner detail DTOs. |
| [`Queries/GetPartnersName/GetPartnersSelectionListQuery.cs`](AdminAreaManagement.Application/Partners/Queries/GetPartnersName/GetPartnersSelectionListQuery.cs) | Filters, sorts and pages the compact partner selection projection. |

### Professions

| File | Purpose |
| --- | --- |
| [`Commands/DeleteProfession/DeleteProfessionCommand.cs`](AdminAreaManagement.Application/Professions/Commands/DeleteProfession/DeleteProfessionCommand.cs) | Toggles the soft-deletion state of a profession by ID. |
| [`Commands/UpsertProfession/UpsertProfessionCommand.cs`](AdminAreaManagement.Application/Professions/Commands/UpsertProfession/UpsertProfessionCommand.cs) | Creates a profession or renames an existing profession and returns its ID. |
| [`Commands/UpsertProfession/UpsertProfessionCommandValidator.cs`](AdminAreaManagement.Application/Professions/Commands/UpsertProfession/UpsertProfessionCommandValidator.cs) | Requires a unique, nonempty profession name of at most 100 characters. |
| [`Queries/GetProfessionsListQuery.cs`](AdminAreaManagement.Application/Professions/Queries/GetProfessionsListQuery.cs) | Filters, sorts and pages the profession reference-data projection. |
| [`Queries/ProfessionDto.cs`](AdminAreaManagement.Application/Professions/Queries/ProfessionDto.cs) | Projects a profession reference to its ID and name. |
| [`Queries/ProfessionsListVm.cs`](AdminAreaManagement.Application/Professions/Queries/ProfessionsListVm.cs) | Wraps a list of profession DTOs. |

### Schools

| File | Purpose |
| --- | --- |
| [`Commands/CreateSchool/CreateSchoolCommand.cs`](AdminAreaManagement.Application/Schools/Commands/CreateSchool/CreateSchoolCommand.cs) | Creates a school reference from a name and locality and returns its ID. |
| [`Commands/DeleteSchool/DeleteSchoolCommand.cs`](AdminAreaManagement.Application/Schools/Commands/DeleteSchool/DeleteSchoolCommand.cs) | Toggles a school's soft-deletion flag by its school ID. |
| [`Commands/UpdateSchool/UpdateSchoolCommand.cs`](AdminAreaManagement.Application/Schools/Commands/UpdateSchool/UpdateSchoolCommand.cs) | Updates an existing school's name and locality and returns its ID. |
| [`Common/SchoolDto.cs`](AdminAreaManagement.Application/Schools/Common/SchoolDto.cs) | Projects a school reference to its ID, name and locality. |
| [`Queries/GetSchoolList/GetSchoolListQuery.cs`](AdminAreaManagement.Application/Schools/Queries/GetSchoolList/GetSchoolListQuery.cs) | Filters, sorts and pages the school reference-data projection. |

### Staffs

| File | Purpose |
| --- | --- |
| [`Commands/CreateStaffmember/CreateStaffMemberCommand.cs`](AdminAreaManagement.Application/Staffs/Commands/CreateStaffmember/CreateStaffMemberCommand.cs) | Creates a staff profile linked to an active organisation membership, saves it with its staged projection and requests outbox dispatch. |
| [`Commands/CreateStaffmember/CreateStaffMemberCommandValidator.cs`](AdminAreaManagement.Application/Staffs/Commands/CreateStaffmember/CreateStaffMemberCommandValidator.cs) | Rejects staff creation when the supplied user name already belongs to the selected team. |
| [`Commands/CreateStaffmember/IntegrationEvents/Events/SocialWorkerCreatedEvent.cs`](AdminAreaManagement.Application/Staffs/Commands/CreateStaffmember/IntegrationEvents/Events/SocialWorkerCreatedEvent.cs) | Defines the social-worker creation event with identity, team and tenant fields. |
| [`Commands/DeleteStaffmember/DeleteStaffCommand.cs`](AdminAreaManagement.Application/Staffs/Commands/DeleteStaffmember/DeleteStaffCommand.cs) | Toggles a staff member's soft-deletion flag, advances its projection version and stages the updated projection for dispatch. |
| [`Commands/UpdateStaffmember/IntegrationEvents/Events/StaffMemberUpdateEvent.cs`](AdminAreaManagement.Application/Staffs/Commands/UpdateStaffmember/IntegrationEvents/Events/StaffMemberUpdateEvent.cs) | Defines the staff update event with name, user name and team fields. |
| [`Commands/UpdateStaffmember/UpdateStaffMemberCommandValidator.cs`](AdminAreaManagement.Application/Staffs/Commands/UpdateStaffmember/UpdateStaffMemberCommandValidator.cs) | Checks staff identifier and name fields and rejects an existing user-name assignment to the selected team. |
| [`Commands/UpdateStaffmember/UpsertStaffCommand.cs`](AdminAreaManagement.Application/Staffs/Commands/UpdateStaffmember/UpsertStaffCommand.cs) | Updates an existing staff profile and team, advances its projection version and stages the revised projection for dispatch. |
| [`DispatchStaffProjectionsCommand.cs`](AdminAreaManagement.Application/Staffs/DispatchStaffProjectionsCommand.cs) | Exposes a tenant-authorized MediatR command for dispatching pending staff projections. |
| [`IStaffProjectionOutbox.cs`](AdminAreaManagement.Application/Staffs/IStaffProjectionOutbox.cs) | Defines outbox staging and dispatch plus the active organisation-membership lookup used when linking staff. |
| [`Queries/GetSocialWorkerQuery.cs`](AdminAreaManagement.Application/Staffs/Queries/GetSocialWorkerQuery.cs) | Looks up a staff member and team and returns the corresponding social-worker details. |
| [`Queries/GetStaffListQuery.cs`](AdminAreaManagement.Application/Staffs/Queries/GetStaffListQuery.cs) | Filters, sorts and pages staff profile results. |
| [`Queries/SocialWorkerDto.cs`](AdminAreaManagement.Application/Staffs/Queries/SocialWorkerDto.cs) | Projects staff identity and team fields into the social-worker DTO. |
| [`Queries/StaffDto.cs`](AdminAreaManagement.Application/Staffs/Queries/StaffDto.cs) | Projects staff profiles with identity and team display fields. |
| [`Queries/StaffListDto.cs`](AdminAreaManagement.Application/Staffs/Queries/StaffListDto.cs) | Wraps staff member DTOs in the referents response collection. |
| [`Queries/UserDto.cs`](AdminAreaManagement.Application/Staffs/Queries/UserDto.cs) | Maps user identity, names and soft-deletion state for staff-related queries. |
| [`Queries/UsersDto.cs`](AdminAreaManagement.Application/Staffs/Queries/UsersDto.cs) | Wraps a list of user DTOs. |

### Teams

| File | Purpose |
| --- | --- |
| [`Commands/DeleteTeam/DeleteTeamCommand.cs`](AdminAreaManagement.Application/Teams/Commands/DeleteTeam/DeleteTeamCommand.cs) | Toggles the soft-deletion flag for a team and saves the change. |
| [`Commands/DeleteTeam/DeleteTeamCommandValidator.cs`](AdminAreaManagement.Application/Teams/Commands/DeleteTeam/DeleteTeamCommandValidator.cs) | Rejects deleting a team that still has staff members. |
| [`Commands/UpsertTeam/UpsertTeamCommand.cs`](AdminAreaManagement.Application/Teams/Commands/UpsertTeam/UpsertTeamCommand.cs) | Creates a team or updates its name and acronym and returns its ID. |
| [`Commands/UpsertTeam/UpsertTeamCommandValidator.cs`](AdminAreaManagement.Application/Teams/Commands/UpsertTeam/UpsertTeamCommandValidator.cs) | Requires a nonempty team name of at most 200 characters. |
| [`Queries/GetTeamsByNameQuery.cs`](AdminAreaManagement.Application/Teams/Queries/GetTeamsByNameQuery.cs) | Checks whether a trimmed team name has a match according to the team repository. |
| [`Queries/GetTeamsListQuery.cs`](AdminAreaManagement.Application/Teams/Queries/GetTeamsListQuery.cs) | Filters, sorts and pages the team projection. |
| [`Queries/TeamDto.cs`](AdminAreaManagement.Application/Teams/Queries/TeamDto.cs) | Maps a team to its ID, name and acronym. |
| [`Queries/TeamsDto.cs`](AdminAreaManagement.Application/Teams/Queries/TeamsDto.cs) | Wraps a list of team DTOs. |

### TrainingFields

| File | Purpose |
| --- | --- |
| [`Common/TrainingFieldDto.cs`](AdminAreaManagement.Application/TrainingFields/Common/TrainingFieldDto.cs) | Projects a training field reference to its ID and name. |
| [`Queries/GetTrainingFieldList/GetTrainingFieldListQuery.cs`](AdminAreaManagement.Application/TrainingFields/Queries/GetTrainingFieldList/GetTrainingFieldListQuery.cs) | Filters, sorts and pages the training-field reference projection. |

### Trainings

| File | Purpose |
| --- | --- |
| [`Commands/CreateTraining/CreateTrainingCommand.cs`](AdminAreaManagement.Application/Trainings/Commands/CreateTraining/CreateTrainingCommand.cs) | Creates a training within an existing training field and returns its ID. |
| [`Commands/DeleteTraining/DeleteTrainingCommand.cs`](AdminAreaManagement.Application/Trainings/Commands/DeleteTraining/DeleteTrainingCommand.cs) | Toggles the soft-deletion flag of a training by ID. |
| [`Commands/UpdateTraining/UpdateTrainingCommand.cs`](AdminAreaManagement.Application/Trainings/Commands/UpdateTraining/UpdateTrainingCommand.cs) | Updates a training's name and field after finding both records. |
| [`Common/TrainingDto.cs`](AdminAreaManagement.Application/Trainings/Common/TrainingDto.cs) | Projects training identifiers and names with the associated field ID and name. |
| [`Queries/GetTrainingList/GetTrainingListQuery.cs`](AdminAreaManagement.Application/Trainings/Queries/GetTrainingList/GetTrainingListQuery.cs) | Filters, sorts and pages training projections that include their field details. |

### TrainingTypes

| File | Purpose |
| --- | --- |
| [`Commands/CreateTrainingType/CreateTrainingTypeCommand.cs`](AdminAreaManagement.Application/TrainingTypes/Commands/CreateTrainingType/CreateTrainingTypeCommand.cs) | Persists a new training type by name and returns its ID. |
| [`Commands/DeleteTrainingType/DeleteTrainingTypeCommand.cs`](AdminAreaManagement.Application/TrainingTypes/Commands/DeleteTrainingType/DeleteTrainingTypeCommand.cs) | Toggles the soft-deletion state of a training type by ID. |
| [`Commands/UpdateTrainingType/UpdateTrainingTypeCommand.cs`](AdminAreaManagement.Application/TrainingTypes/Commands/UpdateTrainingType/UpdateTrainingTypeCommand.cs) | Updates the name of an existing training type and returns its ID. |
| [`Common/TrainingTypeDto.cs`](AdminAreaManagement.Application/TrainingTypes/Common/TrainingTypeDto.cs) | Projects a training-type reference to its ID and name. |
| [`Queries/GetTrainingTypeList/GetTrainingTypeListQuery.cs`](AdminAreaManagement.Application/TrainingTypes/Queries/GetTrainingTypeList/GetTrainingTypeListQuery.cs) | Filters, sorts and pages the training-type reference projection. |

## Core

`AdminAreaManagement.Core` Defines domain entities, value objects and contracts used by the application and its persistence adapters.

### Project files

| File | Purpose |
| --- | --- |
| [`AdminAreaManagement.Core.csproj`](AdminAreaManagement.Core/AdminAreaManagement.Core.csproj) | Defines the .NET 8 domain project. |
| [`packages.lock.json`](AdminAreaManagement.Core/packages.lock.json) | Pins the Core project's resolved NuGet dependencies and content hashes for locked restore. |

### Common

| File | Purpose |
| --- | --- |
| [`AggregateRoot.cs`](AdminAreaManagement.Core/Common/AggregateRoot.cs) | Provides a tenant-owned aggregate root with a collection of pending domain events. |
| [`Document.cs`](AdminAreaManagement.Core/Common/Document.cs) | Defines the tenant-owned document base with name, media type, content and description. |
| [`DomainEvent.cs`](AdminAreaManagement.Core/Common/DomainEvent.cs) | Defines the domain event base and the interface for entities holding events. |
| [`Entity.cs`](AdminAreaManagement.Core/Common/Entity.cs) | Defines common entity identity, audit fields and soft-delete state, plus the organisation assignment for tenant-owned entities. |
| [`Filter.cs`](AdminAreaManagement.Core/Common/Filter.cs) | Represents a filter by property identifier and value. |
| [`Order.cs`](AdminAreaManagement.Core/Common/Order.cs) | Represents a sort column and direction. |
| [`QueryStringParameters.cs`](AdminAreaManagement.Core/Common/QueryStringParameters.cs) | Provides the base type for query-string sorting options. |
| [`User.cs`](AdminAreaManagement.Core/Common/User.cs) | Represents a user identity and profile, including soft-deletion state. |
| [`ValueObject.cs`](AdminAreaManagement.Core/Common/ValueObject.cs) | Implements equality and hashing based on a value object's components. |

### Entities

| File | Purpose |
| --- | --- |
| [`City.cs`](AdminAreaManagement.Core/Entities/City.cs) | Models a city reference with a name and country. |
| [`CityText.cs`](AdminAreaManagement.Core/Entities/CityText.cs) | Normalises and validates city text using a Unicode scalar length limit. |
| [`DocumentPartner.cs`](AdminAreaManagement.Core/Entities/DocumentPartner.cs) | Models a partner document and its durable file-write and file-delete operation state. |
| [`Nationality.cs`](AdminAreaManagement.Core/Entities/Nationality.cs) | Models a nationality reference by name. |
| [`Partner.cs`](AdminAreaManagement.Core/Entities/Partner.cs) | Models the partner aggregate, including staff links, agreements, category, status and related documents. |
| [`PartnerAddress.cs`](AdminAreaManagement.Core/Entities/PartnerAddress.cs) | Adds the address fields to the partial `Partner` entity. |
| [`Profession.cs`](AdminAreaManagement.Core/Entities/Profession.cs) | Models a profession reference by name. |
| [`School.cs`](AdminAreaManagement.Core/Entities/School.cs) | Models a school with its name and locality. |
| [`StaffMember.cs`](AdminAreaManagement.Core/Entities/StaffMember.cs) | Models staff profiles and team membership and tracks linked organisation membership and projection version. |
| [`Team.cs`](AdminAreaManagement.Core/Entities/Team.cs) | Models an organisation-owned team, its acronym and its staff members. |
| [`Training.cs`](AdminAreaManagement.Core/Entities/Training.cs) | Models a training record and its linked training field. |
| [`TrainingField.cs`](AdminAreaManagement.Core/Entities/TrainingField.cs) | Models a named training field. |
| [`TrainingType.cs`](AdminAreaManagement.Core/Entities/TrainingType.cs) | Models a named training type. |

### Enums

| File | Purpose |
| --- | --- |
| [`CategoryOfJob.cs`](AdminAreaManagement.Core/Enums/CategoryOfJob.cs) | Defines the job category values. |
| [`CategoryOfPartner.cs`](AdminAreaManagement.Core/Enums/CategoryOfPartner.cs) | Defines the partner category values. |
| [`EmploymentStatus.cs`](AdminAreaManagement.Core/Enums/EmploymentStatus.cs) | Defines full-time and part-time employment values. |
| [`Gender.cs`](AdminAreaManagement.Core/Enums/Gender.cs) | Defines the gender values used by the domain. |
| [`Reward.cs`](AdminAreaManagement.Core/Enums/Reward.cs) | Defines the job reward categories used by the domain. |
| [`StatusOfJob.cs`](AdminAreaManagement.Core/Enums/StatusOfJob.cs) | Defines active, inactive and all-job status values. |
| [`StatusOfJobOffer.cs`](AdminAreaManagement.Core/Enums/StatusOfJobOffer.cs) | Defines open and closed job-offer states. |
| [`StatusOfPartner.cs`](AdminAreaManagement.Core/Enums/StatusOfPartner.cs) | Defines active, inactive and stopped partner states. |
| [`TypeOfJob.cs`](AdminAreaManagement.Core/Enums/TypeOfJob.cs) | Distinguishes new and replacement jobs. |

### Exceptions

| File | Purpose |
| --- | --- |
| [`AlreadyOccupiedException.cs`](AdminAreaManagement.Core/Exceptions/AlreadyOccupiedException.cs) | Defines the domain exception for an already occupied position. |
| [`CannotHireException.cs`](AdminAreaManagement.Core/Exceptions/CannotHireException.cs) | Defines the domain exception raised when a hire is disallowed. |
| [`InvalidAcronymFormatException.cs`](AdminAreaManagement.Core/Exceptions/InvalidAcronymFormatException.cs) | Defines the domain exception for an invalid acronym format. |
| [`InvalidEmailException.cs`](AdminAreaManagement.Core/Exceptions/InvalidEmailException.cs) | Defines the domain exception for an invalid email address. |
| [`StringSizeLimitException.cs`](AdminAreaManagement.Core/Exceptions/StringSizeLimitException.cs) | Defines the domain exception for a string outside its permitted size. |

### Interfaces

| File | Purpose |
| --- | --- |
| [`IBackgroundTaskQueue.cs`](AdminAreaManagement.Core/Interfaces/IBackgroundTaskQueue.cs) | Defines the contract for queuing and retrieving background work. |
| [`ICityRepository.cs`](AdminAreaManagement.Core/Interfaces/ICityRepository.cs) | Defines the domain-facing repository contract for cities. |
| [`ICurrentUserService.cs`](AdminAreaManagement.Core/Interfaces/ICurrentUserService.cs) | Defines the contract for retrieving the current user's identity. |
| [`IDateTime.cs`](AdminAreaManagement.Core/Interfaces/IDateTime.cs) | Provides a clock interface for callers that need the current time. |
| [`IDocumentPartnerRepository.cs`](AdminAreaManagement.Core/Interfaces/IDocumentPartnerRepository.cs) | Defines the repository contract for partner documents. |
| [`IDocumentRepository.cs`](AdminAreaManagement.Core/Interfaces/IDocumentRepository.cs) | Defines the generic document repository contract. |
| [`IDomainEventService.cs`](AdminAreaManagement.Core/Interfaces/IDomainEventService.cs) | Defines the contract for publishing domain events. |
| [`IFileService.cs`](AdminAreaManagement.Core/Interfaces/IFileService.cs) | Defines the application-facing contract for file storage operations. |
| [`IGenericReadRepository.cs`](AdminAreaManagement.Core/Interfaces/IGenericReadRepository.cs) | Defines the reusable read-only repository contract. |
| [`INationalityRepository.cs`](AdminAreaManagement.Core/Interfaces/INationalityRepository.cs) | Defines the domain-facing repository contract for nationalities. |
| [`INotificationService.cs`](AdminAreaManagement.Core/Interfaces/INotificationService.cs) | Contains a commented-out notification service interface sketch. |
| [`IPartnerRepository.cs`](AdminAreaManagement.Core/Interfaces/IPartnerRepository.cs) | Defines the domain-facing repository contract for partners. |
| [`IProfessionRepository.cs`](AdminAreaManagement.Core/Interfaces/IProfessionRepository.cs) | Defines profession persistence and the uniqueness check for profession names. |
| [`IRepositoryBase.cs`](AdminAreaManagement.Core/Interfaces/IRepositoryBase.cs) | Defines shared repository operations for domain entities. |
| [`IRepositoryManager.cs`](AdminAreaManagement.Core/Interfaces/IRepositoryManager.cs) | Collects the service repositories behind one interface. |
| [`ISchoolRepository.cs`](AdminAreaManagement.Core/Interfaces/ISchoolRepository.cs) | Defines the domain-facing repository contract for schools. |
| [`ISortHelper.cs`](AdminAreaManagement.Core/Interfaces/ISortHelper.cs) | Defines the contract for applying dynamic ordering to query results. |
| [`IStaffRepository.cs`](AdminAreaManagement.Core/Interfaces/IStaffRepository.cs) | Declares the `IStaffMemberRepository` persistence contract. |
| [`ITeamRepository.cs`](AdminAreaManagement.Core/Interfaces/ITeamRepository.cs) | Defines team persistence and a check for existing staff membership. |
| [`ITrainingFieldRepository.cs`](AdminAreaManagement.Core/Interfaces/ITrainingFieldRepository.cs) | Defines the domain-facing repository contract for training fields. |
| [`ITrainingsRepository.cs`](AdminAreaManagement.Core/Interfaces/ITrainingsRepository.cs) | Defines the domain-facing repository contract for trainings. |
| [`ITrainingTypeRepository.cs`](AdminAreaManagement.Core/Interfaces/ITrainingTypeRepository.cs) | Defines the domain-facing repository contract for training types. |

### ValueObjects

| File | Purpose |
| --- | --- |
| [`Address.cs`](AdminAreaManagement.Core/ValueObjects/Address.cs) | Models a street address and its value-based equality. |
| [`ContactDetails.cs`](AdminAreaManagement.Core/ValueObjects/ContactDetails.cs) | Defines `ContactPerson`, including its partner and organisation link and deletion flag. |
| [`Email.cs`](AdminAreaManagement.Core/ValueObjects/Email.cs) | Defines a partner email value object linked to its organisation. |

## Infrastructure

`AdminAreaManagement.Infrastructure` Implements database persistence, tenant authority integration and background processing, including the staff projection outbox and retry workers.

### Project files

| File | Purpose |
| --- | --- |
| [`AdminAreaManagement.Infrastructure.csproj`](AdminAreaManagement.Infrastructure/AdminAreaManagement.Infrastructure.csproj) | Defines the infrastructure project, its persistence and authentication dependencies, and its embedded RLS manifest. |
| [`DependencyInjection.cs`](AdminAreaManagement.Infrastructure/DependencyInjection.cs) | Registers infrastructure services and enables the export and closure participants only for designated evidence fixture tenants. |
| [`packages.lock.json`](AdminAreaManagement.Infrastructure/packages.lock.json) | Pins the resolved NuGet dependency graph for the .NET 8 infrastructure project. |
| [`TenantRegistration.cs`](AdminAreaManagement.Infrastructure/TenantRegistration.cs) | Wires tenant enforcement, AuthManagement access clients, worker credentials, and the staff projection retry worker. |

### Authorization

| File | Purpose |
| --- | --- |
| [`CurrentTenantAccessClient.cs`](AdminAreaManagement.Infrastructure/Authorization/CurrentTenantAccessClient.cs) | Resolves current organisation access through AuthManagement, with strict response validation and one bounded worker credential retry. |
| [`StaffMembershipClient.cs`](AdminAreaManagement.Infrastructure/Authorization/StaffMembershipClient.cs) | Checks whether a staff member has active organisation membership through AuthManagement. |

### Closures

| File | Purpose |
| --- | --- |
| [`AdminAreaClosureFixtureScope.cs`](AdminAreaManagement.Infrastructure/Closures/AdminAreaClosureFixtureScope.cs) | Rejects LIFE-02 closure commands outside the designated evidence fixture tenant. |
| [`AdminAreaClosureGate.cs`](AdminAreaManagement.Infrastructure/Closures/AdminAreaClosureGate.cs) | Takes an organisation advisory lock and blocks ordinary access while the closure fence is active. |
| [`AdminAreaClosureParticipant.cs`](AdminAreaManagement.Infrastructure/Closures/AdminAreaClosureParticipant.cs) | Enters and releases closure fences for the fixture participant and persists its inbox and outbox records. |
| [`AdminAreaClosureRecoveryCapability.cs`](AdminAreaManagement.Infrastructure/Closures/AdminAreaClosureRecoveryCapability.cs) | Invokes PostgreSQL’s restricted closure-fence release function through a separate recovery capability. |

### Exports

| File | Purpose |
| --- | --- |
| [`AdminAreaExportParticipant.cs`](AdminAreaManagement.Infrastructure/Exports/AdminAreaExportParticipant.cs) | Enters export fences, stages deterministic tenant export fragments, and releases fences for the LIFE-01 fixture. |
| [`AdminAreaFixtureScope.cs`](AdminAreaManagement.Infrastructure/Exports/AdminAreaFixtureScope.cs) | Restricts export materialization to the designated LIFE-01 evidence fixture tenant. |
| [`DeterministicFixtureExportArtifactStore.cs`](AdminAreaManagement.Infrastructure/Exports/DeterministicFixtureExportArtifactStore.cs) | Writes and verifies content-addressed export artifacts in a confined non-production fixture directory. |

### Identity

| File | Purpose |
| --- | --- |
| [`ApplicationUser.cs`](AdminAreaManagement.Infrastructure/Identity/ApplicationUser.cs) | Declares the AdminArea ASP.NET Core Identity user type. |
| [`IdentityResultExtensions.cs`](AdminAreaManagement.Infrastructure/Identity/IdentityResultExtensions.cs) | Retains a commented-out adapter from ASP.NET Core Identity results to application results. |
| [`IdentityService.cs`](AdminAreaManagement.Infrastructure/Identity/IdentityService.cs) | Retains a commented-out implementation of the ASP.NET Core Identity service. |

### Messaging

| File | Purpose |
| --- | --- |
| [`StaffProjectionChangedV1.cs`](AdminAreaManagement.Infrastructure/Messaging/StaffProjectionChangedV1.cs) | Defines the versioned full staff projection event, including deletion tombstones. |
| [`StaffProjectionOutbox.cs`](AdminAreaManagement.Infrastructure/Messaging/StaffProjectionOutbox.cs) | Stages staff projection events and dispatches durable outbox rows behind the organisation closure gate and publisher safeguards. |
| [`StaffProjectionRetryWorker.cs`](AdminAreaManagement.Infrastructure/Messaging/StaffProjectionRetryWorker.cs) | Rechecks live worker access for each organisation before retrying its staff projection outbox. |

### Migrations

| File | Purpose |
| --- | --- |
| [`20250427103057_Initial Migration.cs`](AdminAreaManagement.Infrastructure/Migrations/20250427103057_Initial Migration.cs) | Creates the initial AdminArea tables, indexes, and reference seed data. |
| [`20250427103057_Initial Migration.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20250427103057_Initial Migration.Designer.cs) | Records EF Core’s target model for the initial AdminArea migration. |
| [`20260904124303_OrganisationTenantConstraints.cs`](AdminAreaManagement.Infrastructure/Migrations/20260904124303_OrganisationTenantConstraints.cs) | Adds organisation-scoped keys, foreign keys, and indexes to prevent cross-tenant relationships. |
| [`20260904124303_OrganisationTenantConstraints.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260904124303_OrganisationTenantConstraints.Designer.cs) | Records EF Core’s target model after the organisation-scoped relationship constraints. |
| [`20260907185334_CityReferenceIdentity.cs`](AdminAreaManagement.Infrastructure/Migrations/20260907185334_CityReferenceIdentity.cs) | Validates and normalizes city names and countries, then enforces unique active city identities. |
| [`20260907185334_CityReferenceIdentity.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260907185334_CityReferenceIdentity.Designer.cs) | Records EF Core’s target model for normalized city identity. |
| [`20260910165920_ExplicitTenantEnforcement.cs`](AdminAreaManagement.Infrastructure/Migrations/20260910165920_ExplicitTenantEnforcement.cs) | Adds mandatory staff membership, projection versions, and the tenant-scoped staff projection outbox table. |
| [`20260910165920_ExplicitTenantEnforcement.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260910165920_ExplicitTenantEnforcement.Designer.cs) | Records EF Core’s target model for explicit staff membership and projection enforcement. |
| [`20260912180000_PostgreSqlRlsAndRuntimeRoles.cs`](AdminAreaManagement.Infrastructure/Migrations/20260912180000_PostgreSqlRlsAndRuntimeRoles.cs) | Installs the PostgreSQL organisation context function and enforces row-level security for the AdminArea runtime role. |
| [`20260917212648_DurableDocumentFileOperations.cs`](AdminAreaManagement.Infrastructure/Migrations/20260917212648_DurableDocumentFileOperations.cs) | Adds durable file content, operation identifiers, and retry state to partner document records. |
| [`20260917212648_DurableDocumentFileOperations.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260917212648_DurableDocumentFileOperations.Designer.cs) | Records EF Core’s target model for durable partner document file operations. |
| [`20260928074338_Life01AdminAreaExportParticipant.cs`](AdminAreaManagement.Infrastructure/Migrations/20260928074338_Life01AdminAreaExportParticipant.cs) | Adds export fence, inbox, outbox, and fragment tables with tenant policies and write guards. |
| [`20260928074338_Life01AdminAreaExportParticipant.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260928074338_Life01AdminAreaExportParticipant.Designer.cs) | Records EF Core’s target model for the LIFE-01 AdminArea export participant. |
| [`20260928150135_Life02AdminAreaClosureParticipant.cs`](AdminAreaManagement.Infrastructure/Migrations/20260928150135_Life02AdminAreaClosureParticipant.cs) | Adds closure fence, inbox, and outbox tables, tenant policies, write guards, and the restricted fence release function. |
| [`20260928150135_Life02AdminAreaClosureParticipant.Designer.cs`](AdminAreaManagement.Infrastructure/Migrations/20260928150135_Life02AdminAreaClosureParticipant.Designer.cs) | Records EF Core’s target model for the LIFE-02 AdminArea closure participant. |
| [`ApplicationDbContextModelSnapshot.cs`](AdminAreaManagement.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs) | Records the current EF Core model as the baseline for future AdminArea migrations. |

### Persistence

| File | Purpose |
| --- | --- |
| [`ApplicationDbContext.cs`](AdminAreaManagement.Infrastructure/Persistence/ApplicationDbContext.cs) | Defines the AdminArea EF Core entity model and the guarded tenant runtime context. |
| [`CityQueries.cs`](AdminAreaManagement.Infrastructure/Persistence/CityQueries.cs) | Checks for active normalized city identities and returns sorted, paginated city lookup results. |
| [`CityRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/CityRepository.cs) | Persists cities, retrieves them by identifier, and marks them as soft deleted. |
| [`Closures/AdminAreaClosureRecords.cs`](AdminAreaManagement.Infrastructure/Persistence/Closures/AdminAreaClosureRecords.cs) | Defines the tenant-owned persistent fence, inbox, and outbox records for AdminArea closure. |
| [`Configurations/AdminAreaClosureConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/AdminAreaClosureConfiguration.cs) | Maps closure records, including their tenant-aware keys and fence uniqueness constraints. |
| [`Configurations/AdminAreaExportConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/AdminAreaExportConfiguration.cs) | Maps export records, including tenant-aware keys and the single active fence constraint. |
| [`Configurations/CityConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/CityConfiguration.cs) | Maps city records, normalized identity fields, and active-city uniqueness rules. |
| [`Configurations/ConfigurationKeys.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/ConfigurationKeys.cs) | Declares configuration section keys for rewards and closure reasons. |
| [`Configurations/DocumentPartnerConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/DocumentPartnerConfiguration.cs) | Maps documents to tenant-owned partners and enforces unique create and delete operation identifiers per organisation. |
| [`Configurations/NationalityConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/NationalityConfiguration.cs) | Maps nationality reference data and its identifier column. |
| [`Configurations/PartnerConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/PartnerConfiguration.cs) | Maps partners, owned addresses, and organisation-scoped staff, contact, and email relationships. |
| [`Configurations/PartnerDependentConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/PartnerDependentConfiguration.cs) | Maps partner contact persons and emails with organisation-aware composite keys. |
| [`Configurations/ProfessionConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/ProfessionConfiguration.cs) | Maps profession reference data fields. |
| [`Configurations/SchoolConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/SchoolConfiguration.cs) | Maps school reference data fields and their primary key. |
| [`Configurations/StaffConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/StaffConfiguration.cs) | Maps staff with organisation-scoped usernames, membership constraints, and team relationships. |
| [`Configurations/TeamConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/TeamConfiguration.cs) | Maps teams and enforces acronym uniqueness within each organisation. |
| [`Configurations/TrainingConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/TrainingConfiguration.cs) | Maps training records and their primary key. |
| [`Configurations/TrainingFieldConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/TrainingFieldConfiguration.cs) | Maps training field records and their identifier column. |
| [`Configurations/TrainingTypeConfiguration.cs`](AdminAreaManagement.Infrastructure/Persistence/Configurations/TrainingTypeConfiguration.cs) | Maps training type records and their identifier column. |
| [`DatabaseConnectivityHealthCheck.cs`](AdminAreaManagement.Infrastructure/Persistence/DatabaseConnectivityHealthCheck.cs) | Checks database connectivity independently of the guarded tenant DbContext. |
| [`DependencyInjection.cs`](AdminAreaManagement.Infrastructure/Persistence/DependencyInjection.cs) | Registers the tenant-guarded DbContext, persistence services, repositories, and connectivity health check. |
| [`DocumentPartnerRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/DocumentPartnerRepository.cs) | Persists and queries partner document metadata and queues durable file creation and deletion. |
| [`Exports/AdminAreaExportRecords.cs`](AdminAreaManagement.Infrastructure/Persistence/Exports/AdminAreaExportRecords.cs) | Defines the tenant-owned persistent fence, inbox, outbox, and fragment records for AdminArea exports. |
| [`GenericReadRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/GenericReadRepository.cs) | Exposes an in-memory collection through the generic read repository interface. |
| [`Helpers/FileRepositoryHelper.cs`](AdminAreaManagement.Infrastructure/Persistence/Helpers/FileRepositoryHelper.cs) | Derives a partner document folder name from the numeric partner identifier. |
| [`Helpers/PersistenceJsonDIExtensions.cs`](AdminAreaManagement.Infrastructure/Persistence/Helpers/PersistenceJsonDIExtensions.cs) | Declares an empty placeholder for persistence JSON service extensions. |
| [`Helpers/StringExtensions.cs`](AdminAreaManagement.Infrastructure/Persistence/Helpers/StringExtensions.cs) | Adds substring matching with an explicit string comparison. |
| [`Helpers/ToStringUtcExtension.cs`](AdminAreaManagement.Infrastructure/Persistence/Helpers/ToStringUtcExtension.cs) | Formats a DateTime as UTC text. |
| [`MigrationContextFactory.cs`](AdminAreaManagement.Infrastructure/Persistence/MigrationContextFactory.cs) | Supplies a design-time deployment DbContext for migration scaffolding and SQL generation. |
| [`NationalityRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/NationalityRepository.cs) | Stores, filters, and soft-deletes nationality catalog entries. |
| [`PartnerRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/PartnerRepository.cs) | Stores and filters partner records and supports soft deletion. |
| [`ProfessionRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/ProfessionRepository.cs) | Stores, filters, and soft-deletes professions and checks name uniqueness. |
| [`RepositoryBase.cs`](AdminAreaManagement.Infrastructure/Persistence/RepositoryBase.cs) | Provides reusable EF Core queries and create, update, and delete operations with explicit query tracking control. |
| [`RepositoryManager.cs`](AdminAreaManagement.Infrastructure/Persistence/RepositoryManager.cs) | Provides the service’s repositories over one shared DbContext and saves their pending changes. |
| [`SchoolRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/SchoolRepository.cs) | Stores, filters, and soft-deletes school records. |
| [`Seed/SeedData.cs`](AdminAreaManagement.Infrastructure/Persistence/Seed/SeedData.cs) | Seeds cities, nationalities, professions, schools, trainings, training fields, and training types. |
| [`StaffMemberRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/StaffMemberRepository.cs) | Stores and filters staff members, checks team membership, and supports soft deletion. |
| [`TeamRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/TeamRepository.cs) | Stores, filters, and soft-deletes teams and checks uniqueness and staff assignments. |
| [`TenantTransactionAttempt.cs`](AdminAreaManagement.Infrastructure/Persistence/TenantTransactionAttempt.cs) | Guards tenant-scoped transactions, coordinates post-commit actions, and distinguishes uncertain commits. |
| [`TrainingFieldRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/TrainingFieldRepository.cs) | Stores, filters, and soft-deletes training field records. |
| [`TrainingsRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/TrainingsRepository.cs) | Stores, filters, and soft-deletes training records. |
| [`TrainingTypeRepository.cs`](AdminAreaManagement.Infrastructure/Persistence/TrainingTypeRepository.cs) | Stores, filters, and soft-deletes training type records. |

### Security

| File | Purpose |
| --- | --- |
| [`rls-manifest.v10.json`](AdminAreaManagement.Infrastructure/Security/rls-manifest.v10.json) | Declares expected database roles, policies, privileges, and functions for AdminArea RLS verification. |

### Services

| File | Purpose |
| --- | --- |
| [`CurrentUserService.cs`](AdminAreaManagement.Infrastructure/Services/CurrentUserService.cs) | Reads the current user’s ID and name from the HTTP request principal. |
| [`DateTimeService.cs`](AdminAreaManagement.Infrastructure/Services/DateTimeService.cs) | Supplies local system time through the application clock interface. |

## Tests

`Tests` Covers domain rules, application behavior, database migrations and tenant enforcement, and infrastructure integration.

### Application.IntegrationTests

| File | Purpose |
| --- | --- |
| [`Application.IntegrationTests.csproj`](Tests/Application.IntegrationTests/Application.IntegrationTests.csproj) | Defines the .NET 8 application integration test project with AdminArea infrastructure and PostgreSQL fixture dependencies. |
| [`GlobalUsings.cs`](Tests/Application.IntegrationTests/GlobalUsings.cs) | Empty placeholder for application integration test global usings. |
| [`packages.lock.json`](Tests/Application.IntegrationTests/packages.lock.json) | Locks resolved NuGet dependencies for the application integration tests. |
| [`TenantPersistenceMigrationTests.cs`](Tests/Application.IntegrationTests/TenantPersistenceMigrationTests.cs) | Checks tenant migration backfill, rollback on ambiguous legacy data, preserved reference identities, and shared database connection registration. |

### Application.UnitTests

| File | Purpose |
| --- | --- |
| [`Application.UnitTests.csproj`](Tests/Application.UnitTests/Application.UnitTests.csproj) | Defines the .NET 8 application unit test project and references the application and domain projects. |
| [`Cities/CityQueryAndRestorationTests.cs`](Tests/Application.UnitTests/Cities/CityQueryAndRestorationTests.cs) | Tests city restoration uniqueness, deletion behavior, paginated queries, cancellation, and persistence-port boundaries. |
| [`Cities/CreateCityCommandValidatorTests.cs`](Tests/Application.UnitTests/Cities/CreateCityCommandValidatorTests.cs) | Tests required city fields, normalized Unicode lengths, uniqueness checks, and cancellation propagation. |
| [`DocumentPartner/DocumentPartnersTest.cs`](Tests/Application.UnitTests/DocumentPartner/DocumentPartnersTest.cs) | Tests document handler failures for a missing partner and a failing repository. |
| [`DocumentPartner/FileServiceTests.cs`](Tests/Application.UnitTests/DocumentPartner/FileServiceTests.cs) | Tests contained and idempotent document storage, overwrite refusal, invalid identities, and symbolic-link rejection. |
| [`GlobalUsings.cs`](Tests/Application.UnitTests/GlobalUsings.cs) | Empty placeholder for application unit test global usings. |
| [`packages.lock.json`](Tests/Application.UnitTests/packages.lock.json) | Locks resolved NuGet dependencies for the application unit tests. |
| [`Professions/DeleteProfessionCommandHandlerTests.cs`](Tests/Application.UnitTests/Professions/DeleteProfessionCommandHandlerTests.cs) | Tests toggling profession soft deletion and reporting a missing profession. |
| [`Professions/UpsertProfessionCommandTest.cs`](Tests/Application.UnitTests/Professions/UpsertProfessionCommandTest.cs) | Tests profession creation, updates, trimmed names, and failure for an unknown update target. |
| [`Schools/CreateSchoolCommandHandlerTests.cs`](Tests/Application.UnitTests/Schools/CreateSchoolCommandHandlerTests.cs) | Tests school creation and rejection of missing names or localities. |
| [`Teams/UpsertTeamCommandHandlerTests.cs`](Tests/Application.UnitTests/Teams/UpsertTeamCommandHandlerTests.cs) | Tests that an unresolved persisted team identity returns not found without saving. |
| [`TenantAuthorizationTests.cs`](Tests/Application.UnitTests/TenantAuthorizationTests.cs) | Tests explicit permissions, forged tenant refusal, membership identity, platform permission boundaries, and isolated operation contexts. |
| [`TenantOperationCompatibilityTests.cs`](Tests/Application.UnitTests/TenantOperationCompatibilityTests.cs) | Tests fresh authority decisions, strict grant matching, fail-closed response validation, and redacted integrity signals. |

### Domain.UnitTests

| File | Purpose |
| --- | --- |
| [`CityTests.cs`](Tests/Domain.UnitTests/CityTests.cs) | Tests city construction, required fields, Unicode normalization and scalar length, and malformed text rejection. |
| [`Common/DateTimeProvider.cs`](Tests/Domain.UnitTests/Common/DateTimeProvider.cs) | Exposes local and UTC time and honors the active test clock override. |
| [`Common/DateTimeProviderContext.cs`](Tests/Domain.UnitTests/Common/DateTimeProviderContext.cs) | Manages a per-thread stack of test clock overrides and restores the previous override on disposal. |
| [`Domain.UnitTests.csproj`](Tests/Domain.UnitTests/Domain.UnitTests.csproj) | Defines the .NET 8 domain unit test project and references AdminArea core. |
| [`GlobalUsings.cs`](Tests/Domain.UnitTests/GlobalUsings.cs) | Empty placeholder for domain unit test global usings. |
| [`NationalityTests.cs`](Tests/Domain.UnitTests/NationalityTests.cs) | Tests nationality construction and rejection of a null name. |
| [`packages.lock.json`](Tests/Domain.UnitTests/packages.lock.json) | Locks resolved NuGet dependencies for the domain unit tests. |
| [`PartnerTest.cs`](Tests/Domain.UnitTests/PartnerTest.cs) | Tests partner name validation and contact-person assignment and removal. |
| [`ProfessionTests.cs`](Tests/Domain.UnitTests/ProfessionTests.cs) | Tests profession construction, name validation, and name property updates. |
| [`SchoolTests.cs`](Tests/Domain.UnitTests/SchoolTests.cs) | Tests school construction, required name and locality, whitespace preservation, and property updates. |
| [`StaffMemenberTest.cs`](Tests/Domain.UnitTests/StaffMemenberTest.cs) | Tests staff member initialization, argument validation, default user names, full-name formatting, and property updates. |
| [`TeamTest.cs`](Tests/Domain.UnitTests/TeamTest.cs) | Tests that team construction rejects a null name or acronym. |
| [`TenantOwnedEntityTests.cs`](Tests/Domain.UnitTests/TenantOwnedEntityTests.cs) | Tests that an entity's organisation cannot change after its first assignment. |
| [`TrainingFieldTests.cs`](Tests/Domain.UnitTests/TrainingFieldTests.cs) | Tests training field construction, required names, and name property updates. |
| [`TrainingTests.cs`](Tests/Domain.UnitTests/TrainingTests.cs) | Tests training construction, required names and fields, derived field names, and property updates. |
| [`TrainingTypeTests.cs`](Tests/Domain.UnitTests/TrainingTypeTests.cs) | Tests training type construction, missing-name rejection, and name property updates. |

### Infrastructure.IntegrationTests

| File | Purpose |
| --- | --- |
| [`AccessResilienceTests.cs`](Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs) | Tests authority HTTP failure translation, bounded retries, malformed or missing credentials, and redacted metrics and traces. |
| [`AdminAreaClosureParticipantEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/AdminAreaClosureParticipantEvidenceTests.cs) | Tests durable closure boundaries, draining in-flight effects, replay identity, RLS isolation, and safe fence release. |
| [`AdminAreaExportParticipantEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/AdminAreaExportParticipantEvidenceTests.cs) | Tests export fences, exact fragments, tenant isolation, immutable evidence, and fail-closed content-addressed storage. |
| [`AuthorityCaptureRecoveryTests.cs`](Tests/Infrastructure.IntegrationTests/AuthorityCaptureRecoveryTests.cs) | Tests named 503 responses after body-read failures and independent delivery of HTTP and worker authority diagnostics. |
| [`AuthorityResponseCompatibilityTests.cs`](Tests/Infrastructure.IntegrationTests/AuthorityResponseCompatibilityTests.cs) | Tests authority response compatibility, malformed replies, identity contradictions, redacted telemetry, and explicit HTTP outcomes. |
| [`BearerValidationTests.cs`](Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs) | Runs the shared bearer validation contract against AdminArea authentication registration. |
| [`CityReferenceIdentityTests.cs`](Tests/Infrastructure.IntegrationTests/CityReferenceIdentityTests.cs) | Tests PostgreSQL city normalization, uniqueness, concurrent writes and restoration, query behavior, and migration safety. |
| [`DocumentFileOperationEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/DocumentFileOperationEvidenceTests.cs) | Tests durable file-write and delete recovery, idempotent retries, conflict refusal, and replay-safe filesystem effects. |
| [`FirstSqlPathEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/FirstSqlPathEvidenceTests.cs) | Tests first-SQL ordering, tenant refusal before persistence, transaction retries, and server evidence correlation. |
| [`Infrastructure.IntegrationTests.csproj`](Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj) | Defines the infrastructure integration suite and links shared authentication, staff projection, and worker credential tests. |
| [`LocalStackHealthTests.cs`](Tests/Infrastructure.IntegrationTests/LocalStackHealthTests.cs) | Tests local liveness and readiness, safe responses, current readiness results, and authentication on adjacent business routes. |
| [`MembershipMigrationTests.cs`](Tests/Infrastructure.IntegrationTests/MembershipMigrationTests.cs) | Tests membership backfill only from reviewed unambiguous mappings while preserving legacy rows. |
| [`packages.lock.json`](Tests/Infrastructure.IntegrationTests/packages.lock.json) | Locks resolved NuGet dependencies for the infrastructure integration tests. |
| [`PostgreSqlRlsRuntimeEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlRlsRuntimeEvidenceTests.cs) | Tests real PostgreSQL RLS, role and catalog verification, pooled tenant isolation, drift refusal, and recovery prerequisites. |
| [`RuntimeConnectionConfigurationTests.cs`](Tests/Infrastructure.IntegrationTests/RuntimeConnectionConfigurationTests.cs) | Tests runtime connection requirements and registration of the transaction attempt observer across composition roots. |
| [`StaffOutboxTests.cs`](Tests/Infrastructure.IntegrationTests/StaffOutboxTests.cs) | Tests atomic staff and outbox writes, retryable publication, and cross-tenant dependent reparenting refusal. |
| [`StaffProjectionPublicationBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/StaffProjectionPublicationBoundaryTests.cs) | Tests staff projection confirmation, stable event retries, timeout handling, and publication admission under transaction gates. |
| [`StaffRetryWorkerTests.cs`](Tests/Infrastructure.IntegrationTests/StaffRetryWorkerTests.cs) | Tests hosted staff projection recovery, current authorization, closure gates, bounded batches, redacted failures, and startup refusal. |
| [`TenantEnforcementTests.cs`](Tests/Infrastructure.IntegrationTests/TenantEnforcementTests.cs) | Tests persistence classification, tenant filters, forged writes, raw SQL restrictions, parallel isolation, and audited platform access. |
| [`TenantHttpTests.cs`](Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs) | Tests bearer validation, tenant selection, shared application policy, explicit authority response handling, and retry deadlines. |
| [`WorkerAuthorizationRetryTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerAuthorizationRetryTests.cs) | Tests a single credential reload on worker 401, stable scope and deadlines, cancellation, and independent retry budgets. |
| [`WorkerCredentialBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerCredentialBoundaryTests.cs) | Tests early refusal for missing worker credentials or tenant authority without database access or credential adoption. |
