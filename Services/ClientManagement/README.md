# ClientManagement

ClientManagement is the Zeka backend service for beneficiary records and support workflows. It manages beneficiary lookup and dossiers, case assignments, assessments, interactions and follow-up actions. It also maintains a local projection of staff information and participates in organisation export and closure.

The file reference is grouped by project and then by feature or component. Each entry links to its source file.

[API](#api) · [Application](#application) · [Core](#core) · [Infrastructure](#infrastructure) · [Tests](#tests)

## API

`Client.API` Hosts ASP.NET Core endpoints for beneficiary lookup, client records and support cases, along with tenant middleware and report assets.

### Project files

| File | Purpose |
| --- | --- |
| [`appsettings.Development.json`](Client.API/appsettings.Development.json) | Provides local development logging settings. |
| [`appsettings.json`](Client.API/appsettings.json) | Defines baseline logging, tenancy, file-server, organisation, RabbitMQ and EventBus settings. |
| [`Client.API.http`](Client.API/Client.API.http) | Provides example requests for client search and native-language patching. |
| [`ClientManagement.API.csproj`](Client.API/ClientManagement.API.csproj) | Defines the .NET 8 web host and its Application, Infrastructure and shared authentication references. |
| [`DependencyInjection.cs`](Client.API/DependencyInjection.cs) | Registers web services, the current-user adapter, JSON handling, Swagger and CORS. |
| [`Dockerfile`](Client.API/Dockerfile) | Performs the strict locked restore, builds and publishes the API, and packages it for a non-root ASP.NET Core runtime. |
| [`LocalStackHealth.cs`](Client.API/LocalStackHealth.cs) | Registers the LocalStack-only `/health/live` and `/health/ready` probes before business authentication. |
| [`packages.lock.json`](Client.API/packages.lock.json) | Pins API NuGet dependencies and content hashes for locked restore. |
| [`Program.cs`](Client.API/Program.cs) | Composes the host and configures dependency registration, health checks, auth, tenant middleware and HTTP endpoints. |
| [`TenantAuthentication.cs`](Client.API/TenantAuthentication.cs) | Registers shared Zeka bearer-token validation for the Client API. |

### Assets

| File | Purpose |
| --- | --- |
| [`Images/Zekalogo.png`](Client.API/Assets/Images/Zekalogo.png) | Stores the Zeka logo image used as an API asset. |
| [`Templates/AssessmentReport.liquid`](Client.API/Assets/Templates/AssessmentReport.liquid) | Renders assessment reports using beneficiary, personal, professional and training information. |

### Controllers

| File | Purpose |
| --- | --- |
| [`ApiControllerBase.cs`](Client.API/Controllers/ApiControllerBase.cs) | Provides the shared controller route prefix and MediatR access. |
| [`BeneficiariesController.cs`](Client.API/Controllers/BeneficiariesController.cs) | Exposes the versioned beneficiary lookup over HTTP. |
| [`ClientsController.cs`](Client.API/Controllers/ClientsController.cs) | Exposes client listing, search, detail, creation and native-language update operations. |
| [`SupportsController.cs`](Client.API/Controllers/SupportsController.cs) | Exposes support lookup by beneficiary or referent, upsert, closure, deletion and reference data. |

### Filters

| File | Purpose |
| --- | --- |
| [`ApiExceptionFilterAttribute.cs`](Client.API/Filters/ApiExceptionFilterAttribute.cs) | Maps application and domain exceptions to HTTP error results. |
| [`AuthorizeCheckOperationFilter.cs`](Client.API/Filters/AuthorizeCheckOperationFilter.cs) | Adds authorization information to the generated Swagger operations. |

### Infrastructure

| File | Purpose |
| --- | --- |
| [`CustomExceptionHandler.cs`](Client.API/Infrastructure/CustomExceptionHandler.cs) | Defines an exception handler for validation, not-found and access errors. |
| [`EndpointGroupBase.cs`](Client.API/Infrastructure/EndpointGroupBase.cs) | Defines the base abstraction for mapping an endpoint group. |
| [`IEndpointRouteBuilderExtensions.cs`](Client.API/Infrastructure/IEndpointRouteBuilderExtensions.cs) | Contains commented-out helpers for registering named minimal API routes. |
| [`MethodInfoExtensions.cs`](Client.API/Infrastructure/MethodInfoExtensions.cs) | Identifies anonymous methods using their generated reflection names. |
| [`WebApplicationExtensions.cs`](Client.API/Infrastructure/WebApplicationExtensions.cs) | Contains a commented-out sketch for discovering and mapping minimal API endpoint groups. |

### Properties

| File | Purpose |
| --- | --- |
| [`launchSettings.json`](Client.API/Properties/launchSettings.json) | Defines local development and IDE launch profiles. |

### Services

| File | Purpose |
| --- | --- |
| [`AuthDependencyUnavailableLog.cs`](Client.API/Services/AuthDependencyUnavailableLog.cs) | Writes the Auth dependency unavailable diagnostic at most once per HTTP request. |
| [`ClientPatch.cs`](Client.API/Services/ClientPatch.cs) | Adapts a JSON patch document to the client patch contract and exposes its replacement values. |
| [`CurrentUserService.cs`](Client.API/Services/CurrentUserService.cs) | Reads the current HTTP user's identity and username. |
| [`TenantAccessMiddleware.cs`](Client.API/Services/TenantAccessMiddleware.cs) | Applies the endpoint's tenant access policy and maps denial or authority unavailability to HTTP results. |
| [`TenantRequestIdentity.cs`](Client.API/Services/TenantRequestIdentity.cs) | Holds the current request's subject, selected organisation and bearer credential for access checks. |
| [`TenantRequestPolicyAttribute.cs`](Client.API/Services/TenantRequestPolicyAttribute.cs) | Associates an HTTP endpoint with its application request type and tenant access policy. |

## Application

`Client.Application` Defines commands, queries, validation and authorization for beneficiary, dossier, assessment, interaction and follow-up workflows.

### Project files

| File | Purpose |
| --- | --- |
| [`ClientManagement.Application.csproj`](Client.Application/ClientManagement.Application.csproj) | Builds the .NET 8 Client application project with its domain, lifecycle, messaging, Mapperly and document-generation dependencies. |
| [`DependencyInjection.cs`](Client.Application/DependencyInjection.cs) | Registers Client handlers, pipeline behaviors, supporting services and the Fluid PDF converter. |
| [`packages.lock.json`](Client.Application/packages.lock.json) | Pins the resolved .NET 8 Client application dependencies for locked restore. |
| [`THIRD-PARTY-NOTICES.md`](Client.Application/THIRD-PARTY-NOTICES.md) | Records Mapperly's build-only dependency status and Apache 2.0 licensing. |

### ActionWrites

| File | Purpose |
| --- | --- |
| [`ActionWriteAuthority.cs`](Client.Application/ActionWrites/ActionWriteAuthority.cs) | Coordinates fresh time-zone, assignee and client authority checks for one action write and rechecks observation freshness before commit. |
| [`ActionWriteContracts.cs`](Client.Application/ActionWrites/ActionWriteContracts.cs) | Defines interaction and follow-up action commands and delegates authorized recording and changes to the action store. |

### AssessmentDocument

| File | Purpose |
| --- | --- |
| [`Commands/GenerateAssessmentDocumentCommand/GenerateAssessmentDocumentCommand.cs`](Client.Application/AssessmentDocument/Commands/GenerateAssessmentDocumentCommand/GenerateAssessmentDocumentCommand.cs) | Builds an assessment document from a visible client, assessment and supporting records using the configured template renderer. |
| [`Commands/GenerateAssessmentDocumentCommand/GenerateAssessmentDocumentCommandValidator.cs`](Client.Application/AssessmentDocument/Commands/GenerateAssessmentDocumentCommand/GenerateAssessmentDocumentCommandValidator.cs) | Requires an assessment ID before generating a document. |

### Assessments

| File | Purpose |
| --- | --- |
| [`Commands/CreateAssessment/CreateAssessmentCommand.cs`](Client.Application/Assessments/Commands/CreateAssessment/CreateAssessmentCommand.cs) | Creates a client assessment with personal circumstances, professional expectations and related profession entries. |
| [`Commands/CreateAssessment/CreateAssessmentCommandValidator.cs`](Client.Application/Assessments/Commands/CreateAssessment/CreateAssessmentCommandValidator.cs) | Requires a client ID and applies the repository's assessment-finalization check before creation. |
| [`Commands/DeleteAssessment/DeleteAssessmentCommand.cs`](Client.Application/Assessments/Commands/DeleteAssessment/DeleteAssessmentCommand.cs) | Soft-deletes an existing assessment and reports a missing assessment ID. |
| [`Commands/FinalizeAssessment/FinalizeAssessmentCommand.cs`](Client.Application/Assessments/Commands/FinalizeAssessment/FinalizeAssessmentCommand.cs) | Finalizes the requested assessment if it is still open and returns its ID. |
| [`Commands/FinalizeAssessment/FinalizeBilanCommandValidator.cs`](Client.Application/Assessments/Commands/FinalizeAssessment/FinalizeBilanCommandValidator.cs) | Checks through the repository that the requested assessment has not already been finalized. |
| [`Commands/UpdateAssessment/UpdateAssessmentCommand.cs`](Client.Application/Assessments/Commands/UpdateAssessment/UpdateAssessmentCommand.cs) | Updates an assessment's personal and professional fields and reconciles its profession entries. |
| [`Commands/UpdateAssessment/UpdateAssessmentCommandValidator.cs`](Client.Application/Assessments/Commands/UpdateAssessment/UpdateAssessmentCommandValidator.cs) | Requires an assessment ID for an update. |
| [`Common/AssessmentDto.cs`](Client.Application/Assessments/Common/AssessmentDto.cs) | Carries assessment status, personal circumstances, professional expectations and associated profession details. |
| [`Common/AssessmentProjection.cs`](Client.Application/Assessments/Common/AssessmentProjection.cs) | Projects assessment and associated profession fields into response DTOs while preserving established field defaults. |
| [`Common/ProfessionalAssessmentDto.cs`](Client.Application/Assessments/Common/ProfessionalAssessmentDto.cs) | Carries profession details and acquired knowledge for an assessment. |
| [`Queries/GetBilansList/BilansListVm.cs`](Client.Application/Assessments/Queries/GetBilansList/BilansListVm.cs) | Wraps a list of assessment DTOs in the Bilans response. |
| [`Queries/GetBilansList/GetArchivedBilansListQuery.cs`](Client.Application/Assessments/Queries/GetBilansList/GetArchivedBilansListQuery.cs) | Projects and pages archived assessments in descending assessment-ID order. |
| [`Queries/GetBilansList/GetAssessmentsListQuery.cs`](Client.Application/Assessments/Queries/GetBilansList/GetAssessmentsListQuery.cs) | Projects and pages assessments for the selected client, including their profession entries. |
| [`Queries/GetCurrentBilan/GetCurrentAssessmentQuery.cs`](Client.Application/Assessments/Queries/GetCurrentBilan/GetCurrentAssessmentQuery.cs) | Retrieves the selected client's current unfinalized assessment. |
| [`Queries/GetLastBilan/GetLastAssessmentQuery.cs`](Client.Application/Assessments/Queries/GetLastBilan/GetLastAssessmentQuery.cs) | Returns the selected client's latest finalized assessment that is not soft-deleted. |

### BeneficiaryB1

| File | Purpose |
| --- | --- |
| [`BeneficiaryB1Contracts.cs`](Client.Application/BeneficiaryB1/BeneficiaryB1Contracts.cs) | Defines beneficiary context and interaction contracts and applies tenant authorization, validation and transaction rules for reading and recording interactions. |

### Clients

| File | Purpose |
| --- | --- |
| [`Commands/AddClient/AddClientCommand.cs`](Client.Application/Clients/Commands/AddClient/AddClientCommand.cs) | Creates and persists a client from the supplied personal, civil status and contact details. |
| [`Commands/Exceptions/ClientBadRequestException.cs`](Client.Application/Clients/Commands/Exceptions/ClientBadRequestException.cs) | Carries an application-level error message for an invalid client request. |
| [`Commands/Exceptions/PatchDocBadRequestException.cs`](Client.Application/Clients/Commands/Exceptions/PatchDocBadRequestException.cs) | Reports an invalid or missing client patch document. |
| [`Commands/UpdateClient/UpdateClientCommand.cs`](Client.Application/Clients/Commands/UpdateClient/UpdateClientCommand.cs) | Defines the client update request carrying a NISS list and currently returns zero without an update. |
| [`Commands/UpdateNativeLanguage/UpdateNativeLanguageCommand.cs`](Client.Application/Clients/Commands/UpdateNativeLanguage/UpdateNativeLanguageCommand.cs) | Looks up a client by NISS, updates the native language and saves the change. |
| [`Commands/UpSertIbisNumber/IClientPatch.cs`](Client.Application/Clients/Commands/UpSertIbisNumber/IClientPatch.cs) | Defines the application contract for applying a client patch and validating replacement values independently of the wire format. |
| [`Commands/UpSertIbisNumber/JsonPatchDocumentValidator.cs`](Client.Application/Clients/Commands/UpSertIbisNumber/JsonPatchDocumentValidator.cs) | Limits each client patch replacement value to 20 characters. |
| [`Commands/UpSertIbisNumber/UpSertIbisNumberCommand.cs`](Client.Application/Clients/Commands/UpSertIbisNumber/UpSertIbisNumberCommand.cs) | Finds a client by NISS and applies the IBIS patch to the returned DTO, while the client entity has no mapped IBIS field. |
| [`Commands/UpSertIbisNumber/UpSertIbisNumberCommandValidator.cs`](Client.Application/Clients/Commands/UpSertIbisNumber/UpSertIbisNumberCommandValidator.cs) | Applies the shared client patch validator to the IBIS upsert request. |
| [`Model/UpdateClientDto.cs`](Client.Application/Clients/Model/UpdateClientDto.cs) | Carries the IBIS number exposed through the client patch request. |
| [`Queries/GetClientDetail/CivilStatusDto.cs`](Client.Application/Clients/Queries/GetClientDetail/CivilStatusDto.cs) | Represents a civil-status option with its numeric value and display label. |
| [`Queries/GetClientDetail/ClientDto.cs`](Client.Application/Clients/Queries/GetClientDetail/ClientDto.cs) | Carries client identity, contact, support, language, training and employment details for the dossier view. |
| [`Queries/GetClientDetail/GenderDto.cs`](Client.Application/Clients/Queries/GetClientDetail/GenderDto.cs) | Represents a gender option with its numeric value and display label. |
| [`Queries/GetClientDetail/GetClientDetailQuery.cs`](Client.Application/Clients/Queries/GetClientDetail/GetClientDetailQuery.cs) | Projects a visible client dossier by ID with support, social worker and team details. |
| [`Queries/GetClients/ClientLookUpDto.cs`](Client.Application/Clients/Queries/GetClients/ClientLookUpDto.cs) | Carries client identifiers and display fields for the legacy lookup. |
| [`Queries/GetClients/ClientLookupProjection.cs`](Client.Application/Clients/Queries/GetClients/ClientLookupProjection.cs) | Projects client records into the legacy lookup DTO. |
| [`Queries/GetClients/ClientsDto.cs`](Client.Application/Clients/Queries/GetClients/ClientsDto.cs) | Wraps a collection of client lookup DTOs. |
| [`Queries/GetClients/GetClientsBySearchTextQuery.cs`](Client.Application/Clients/Queries/GetClients/GetClientsBySearchTextQuery.cs) | Searches clients using supplied text and returns the projected results in name order. |
| [`Queries/GetClients/GetClientsQuery.cs`](Client.Application/Clients/Queries/GetClients/GetClientsQuery.cs) | Returns client lookup results in name order. |
| [`Queries/LookupBeneficiaries/BeneficiaryLookupDto.cs`](Client.Application/Clients/Queries/LookupBeneficiaries/BeneficiaryLookupDto.cs) | Defines the versioned beneficiary lookup response with beneficiary ID, display name and a has-more indicator. |
| [`Queries/LookupBeneficiaries/LookupBeneficiariesQuery.cs`](Client.Application/Clients/Queries/LookupBeneficiaries/LookupBeneficiariesQuery.cs) | Validates the search term and returns up to 20 tenant-visible beneficiaries in stable name and ID order. |

### Common

| File | Purpose |
| --- | --- |
| [`Authorization/ClientRequestActivityClassifier.cs`](Client.Application/Common/Authorization/ClientRequestActivityClassifier.cs) | Classifies MediatR requests as ordinary organisation writes or neutral activity and rejects unclassified commands. |
| [`Authorization/IClientParentLock.cs`](Client.Application/Common/Authorization/IClientParentLock.cs) | Defines the dossier locks and parent lookups that serialize social-case writes with action writes. |
| [`Authorization/IStaffMembershipLink.cs`](Client.Application/Common/Authorization/IStaffMembershipLink.cs) | Checks whether a staff membership belongs to the given organisation and meets the requested activity requirement. |
| [`Authorization/ITenantTransactionExecutor.cs`](Client.Application/Common/Authorization/ITenantTransactionExecutor.cs) | Defines authorized tenant transaction modes, single-attempt execution and the indeterminate-commit failure contract. |
| [`Authorization/PostCommitNotifications.cs`](Client.Application/Common/Authorization/PostCommitNotifications.cs) | Queues in-process notifications for publication after the tenant transaction commits. |
| [`Authorization/TenantAccess.cs`](Client.Application/Common/Authorization/TenantAccess.cs) | Defines current tenant access decisions and establishes a single authorized tenant scope with the required permissions. |
| [`Authorization/TenantAccessDiagnostics.cs`](Client.Application/Common/Authorization/TenantAccessDiagnostics.cs) | Validates permission codes and emits bounded authorization diagnostics without changing the access decision. |
| [`Behaviours/AuthorizationBehaviour.cs`](Client.Application/Common/Behaviours/AuthorizationBehaviour.cs) | Authorizes each request, applies action-write prechecks and final guards where needed, and selects the proper tenant transaction and post-commit handling. |
| [`Behaviours/LoggingBehaviour.cs`](Client.Application/Common/Behaviours/LoggingBehaviour.cs) | Logs the request type and available current-user context before dispatch. |
| [`Behaviours/PerformanceBehaviour.cs`](Client.Application/Common/Behaviours/PerformanceBehaviour.cs) | Measures request duration and logs requests over the performance reporting threshold. |
| [`Behaviours/UnhandledExceptionBehaviour.cs`](Client.Application/Common/Behaviours/UnhandledExceptionBehaviour.cs) | Logs unexpected failure types and request names while preserving the exception and keeping exception text and request details out of that log. |
| [`Behaviours/ValidationBehaviour.cs`](Client.Application/Common/Behaviours/ValidationBehaviour.cs) | Runs applicable request validators and reports grouped failures before handler execution. |
| [`Exceptions/ApplicationExceptionBase.cs`](Client.Application/Common/Exceptions/ApplicationExceptionBase.cs) | Defines the base exception contract for an HTTP status, problem type and title. |
| [`Exceptions/BadRequestException.cs`](Client.Application/Common/Exceptions/BadRequestException.cs) | Defines a base exception carrying a bad-request message. |
| [`Exceptions/FileAccessException.cs`](Client.Application/Common/Exceptions/FileAccessException.cs) | Reports file-access failures with an affected file name and HTTP 405 metadata. |
| [`Exceptions/ForbiddenAccessException.cs`](Client.Application/Common/Exceptions/ForbiddenAccessException.cs) | Signals that an operation is forbidden. |
| [`Exceptions/ForbiddenOperationException.cs`](Client.Application/Common/Exceptions/ForbiddenOperationException.cs) | Reports forbidden candidacy deletion with its identifier and HTTP 405 metadata. |
| [`Exceptions/NotFoundException.cs`](Client.Application/Common/Exceptions/NotFoundException.cs) | Reports missing resources and can include the entity name and lookup key. |
| [`Exceptions/ValidationException.cs`](Client.Application/Common/Exceptions/ValidationException.cs) | Collects validator error messages by property name. |
| [`Helpers/SortHelper.cs`](Client.Application/Common/Helpers/SortHelper.cs) | Applies the requested property sorting to an IQueryable. |
| [`Interfaces/IApplicationDBContextcs.cs`](Client.Application/Common/Interfaces/IApplicationDBContextcs.cs) | Defines the application data context for clients, supports and social workers. |
| [`Interfaces/IDocumentGeneratorService.cs`](Client.Application/Common/Interfaces/IDocumentGeneratorService.cs) | Defines the asynchronous assessment-report generator that returns document bytes. |
| [`Mappings/ClientReadProjections.cs`](Client.Application/Common/Mappings/ClientReadProjections.cs) | Projects quarterly monitoring reports and monitoring actions with Mapperly and builds the support read projection explicitly. |
| [`Mappings/MappingExtensions.cs`](Client.Application/Common/Mappings/MappingExtensions.cs) | Provides the asynchronous pagination extension for projected query results. |
| [`Models/DomainEventNotification.cs`](Client.Application/Common/Models/DomainEventNotification.cs) | Wraps a domain event as a typed MediatR notification. |
| [`Models/File.cs`](Client.Application/Common/Models/File.cs) | Carries a file name and its associated bytes. |
| [`Models/PaginatedList.cs`](Client.Application/Common/Models/PaginatedList.cs) | Builds an asynchronous query page with the total count, paging metadata and items. |
| [`Models/Result.cs`](Client.Application/Common/Models/Result.cs) | Represents success or failure with an error collection. |
| [`Security/AuthorizeAttribute.cs`](Client.Application/Common/Security/AuthorizeAttribute.cs) | Defines class-level authorization metadata for roles and named policies. |
| [`Services/DocumentGeneratorService.cs`](Client.Application/Common/Services/DocumentGeneratorService.cs) | Renders an assessment report with a Fluid HTML template and converts it to PDF. |
| [`Services/DomainEventService.cs`](Client.Application/Common/Services/DomainEventService.cs) | Publishes runtime domain events as their corresponding typed MediatR notifications. |

### Configuration

| File | Purpose |
| --- | --- |
| [`FluidServiceConfiguration.cs`](Client.Application/Configuration/FluidServiceConfiguration.cs) | Defines and validates assessment template, logo and organisation contact settings for document rendering. |

### Exceptions

| File | Purpose |
| --- | --- |
| [`FluidException.cs`](Client.Application/Exceptions/FluidException.cs) | Carries an error message for Fluid template and document-generation failures. |

### Languages

| File | Purpose |
| --- | --- |
| [`Queries/SearchLanguagesQuery.cs`](Client.Application/Languages/Queries/SearchLanguagesQuery.cs) | Searches reference languages and maps the results to language IDs and names. |

### Lifecycle

| File | Purpose |
| --- | --- |
| [`ClientClosureParticipant.cs`](Client.Application/Lifecycle/ClientClosureParticipant.cs) | Defines the Client organisation-closure participant and its fence entry and release operations. |
| [`ClientExportParticipant.cs`](Client.Application/Lifecycle/ClientExportParticipant.cs) | Defines the Client export participant, verified artifact receipts and canonical CSV and hash helpers. |

### Models

| File | Purpose |
| --- | --- |
| [`AssessmentReportDocumentModel.cs`](Client.Application/Models/AssessmentReportDocumentModel.cs) | Carries organisation branding and contact details, assessment and client data, and related supports, school registrations and work experience for the report template. |

### MonitoringActions

| File | Purpose |
| --- | --- |
| [`Commands/CreateAction/CreateMonitoringActionCommand.cs`](Client.Application/MonitoringActions/Commands/CreateAction/CreateMonitoringActionCommand.cs) | Persists a new monitoring-action catalogue entry from its label. |
| [`Commands/CreateAction/CreateMonitoringActionCommandValidator.cs`](Client.Application/MonitoringActions/Commands/CreateAction/CreateMonitoringActionCommandValidator.cs) | Limits a new monitoring-action label to 30 characters. |
| [`Commands/DeleteAction/DeleteMonitoringActionCommand.cs`](Client.Application/MonitoringActions/Commands/DeleteAction/DeleteMonitoringActionCommand.cs) | Finds a monitoring action by ID and soft-deletes the entry. |
| [`Commands/UpdateAction/UpdateMonitoringActionCommand.cs`](Client.Application/MonitoringActions/Commands/UpdateAction/UpdateMonitoringActionCommand.cs) | Finds a monitoring action by ID and persists the revised label. |
| [`Commands/UpdateAction/UpdateMonitoringActionCommandValidator.cs`](Client.Application/MonitoringActions/Commands/UpdateAction/UpdateMonitoringActionCommandValidator.cs) | Limits an updated monitoring-action label to 30 characters. |
| [`Common/MonitoringActionDto.cs`](Client.Application/MonitoringActions/Common/MonitoringActionDto.cs) | Carries a monitoring-action ID and label. |
| [`Queries/GetAll/GetMonitoringActionsQuery.cs`](Client.Application/MonitoringActions/Queries/GetAll/GetMonitoringActionsQuery.cs) | Projects the available monitoring-action catalogue entries into DTOs. |
| [`Queries/GetById/GetMonitoringActionById.cs`](Client.Application/MonitoringActions/Queries/GetById/GetMonitoringActionById.cs) | Projects a single monitoring action from its ID. |

### QuarterlyMonitorings

| File | Purpose |
| --- | --- |
| [`Commands/CreateQuarterlyMonitoring/CreateQuarterlyMonitoringCommand.cs`](Client.Application/QuarterlyMonitorings/Commands/CreateQuarterlyMonitoring/CreateQuarterlyMonitoringCommand.cs) | Persists a quarterly monitoring report after finding its client, staff member and action references. |
| [`Commands/CreateQuarterlyMonitoring/CreateQuarterlyMonitoringCommandValidator.cs`](Client.Application/QuarterlyMonitorings/Commands/CreateQuarterlyMonitoring/CreateQuarterlyMonitoringCommandValidator.cs) | Requires an action date when creating a quarterly monitoring report. |
| [`Commands/DeleteQuarterlyMonitoring/DeleteQuarterlyMonitoringCommand.cs`](Client.Application/QuarterlyMonitorings/Commands/DeleteQuarterlyMonitoring/DeleteQuarterlyMonitoringCommand.cs) | Finds and soft-deletes a quarterly monitoring report by ID. |
| [`Commands/UpdateQuarterlyMonitoring/UpdateQuarterlyMonitoringCommand.cs`](Client.Application/QuarterlyMonitorings/Commands/UpdateQuarterlyMonitoring/UpdateQuarterlyMonitoringCommand.cs) | Validates a quarterly report's referenced records and persists the revised client, staff member, action, date and comment. |
| [`Commands/UpdateQuarterlyMonitoring/UpdateQuarterlyMonitoringCommandValidator.cs`](Client.Application/QuarterlyMonitorings/Commands/UpdateQuarterlyMonitoring/UpdateQuarterlyMonitoringCommandValidator.cs) | Requires an action date when updating a quarterly monitoring report. |
| [`Common/QuarterlyMonitoringDto.cs`](Client.Application/QuarterlyMonitorings/Common/QuarterlyMonitoringDto.cs) | Carries a quarterly report's client, staff member, action, date, comment and deletion status. |
| [`Queries/GetById/GetQuarterlyMonitoringsByIdQuery.cs`](Client.Application/QuarterlyMonitorings/Queries/GetById/GetQuarterlyMonitoringsByIdQuery.cs) | Projects one quarterly monitoring report by ID. |
| [`Queries/GetList/GetQuarterlyMonitoringQueryValidator.cs`](Client.Application/QuarterlyMonitorings/Queries/GetList/GetQuarterlyMonitoringQueryValidator.cs) | Requires positive page number and size for the quarterly-monitoring list. |
| [`Queries/GetList/GetQuarterlyMonitoringsQuery.cs`](Client.Application/QuarterlyMonitorings/Queries/GetList/GetQuarterlyMonitoringsQuery.cs) | Filters and pages quarterly monitoring reports, with a deleted-record option and latest action dates first. |
| [`Queries/GetListByBeneficiary/GetQuarterlyMonitoringsByBeneficiaryQuery.cs`](Client.Application/QuarterlyMonitorings/Queries/GetListByBeneficiary/GetQuarterlyMonitoringsByBeneficiaryQuery.cs) | Filters and pages quarterly monitoring reports for one client, with the latest action dates first. |
| [`Queries/GetListByBeneficiary/GetQuarterlyMonitoringsByBeneficiaryQueryValidator.cs`](Client.Application/QuarterlyMonitorings/Queries/GetListByBeneficiary/GetQuarterlyMonitoringsByBeneficiaryQueryValidator.cs) | Requires positive page number and size for the client-specific quarterly-monitoring list. |
| [`Queries/GetListByReferent/GetQuarterlyMonitoringsByReferentQuery.cs`](Client.Application/QuarterlyMonitorings/Queries/GetListByReferent/GetQuarterlyMonitoringsByReferentQuery.cs) | Filters and pages quarterly monitoring reports for one staff member, with the latest action dates first. |
| [`Queries/GetListByReferent/GetQuarterlyMonitoringsByReferentQueryValidator.cs`](Client.Application/QuarterlyMonitorings/Queries/GetListByReferent/GetQuarterlyMonitoringsByReferentQueryValidator.cs) | Requires positive page number and size for the staff-specific quarterly-monitoring list. |

### SchoolRegistations

| File | Purpose |
| --- | --- |
| [`Commands/CreateSchoolRegistration/CreateSchoolRegistrationCommand.cs`](Client.Application/SchoolRegistations/Commands/CreateSchoolRegistration/CreateSchoolRegistrationCommand.cs) | Creates a client's school registration from training and course details and currently supplies a placeholder school. |
| [`Commands/DeleteSchoolRegistration/DeleteSchoolRegistrationCommand.cs`](Client.Application/SchoolRegistations/Commands/DeleteSchoolRegistration/DeleteSchoolRegistrationCommand.cs) | Toggles the soft-deletion flag on a school registration by ID. |
| [`Commands/UpdateSchoolRegistration/UpdateSchoolRegistrationCommand.cs`](Client.Application/SchoolRegistations/Commands/UpdateSchoolRegistration/UpdateSchoolRegistrationCommand.cs) | Updates course dates, level, result and note while the school lookup still uses a placeholder. |
| [`Common/SchoolRegistrationDto.cs`](Client.Application/SchoolRegistations/Common/SchoolRegistrationDto.cs) | Carries the school registration's client, training, school, dates and outcome details. |
| [`Queries/GetSchoolList/GetSchoolRegistrationsListQuery.cs`](Client.Application/SchoolRegistations/Queries/GetSchoolList/GetSchoolRegistrationsListQuery.cs) | Projects and pages school registrations for a specified client, with filtering and requested sorting. |
| [`Queries/GetSchoolList/SchoolRegistrationListVm.cs`](Client.Application/SchoolRegistations/Queries/GetSchoolList/SchoolRegistrationListVm.cs) | Wraps a list of school registration DTOs. |

### SocialWorker

| File | Purpose |
| --- | --- |
| [`IntegrationEvents/EventHandlers/SocialWorkerCreatedEventHandler.cs`](Client.Application/SocialWorker/IntegrationEvents/EventHandlers/SocialWorkerCreatedEventHandler.cs) | Rejects legacy social-worker events whose payload lacks an immutable membership and projection revision. |
| [`IntegrationEvents/SocialWorkerCreatedEvent.cs`](Client.Application/SocialWorker/IntegrationEvents/SocialWorkerCreatedEvent.cs) | Defines the legacy social-worker-created event with staff, team and tenant fields. |

### Supports

| File | Purpose |
| --- | --- |
| [`Commands/CloseSupport/CloseSupportCommand.cs`](Client.Application/Supports/Commands/CloseSupport/CloseSupportCommand.cs) | Locks the support's dossier and closes the case after rechecking the end date against its start date. |
| [`Commands/CloseSupport/CloseSupportCommandValidator.cs`](Client.Application/Supports/Commands/CloseSupport/CloseSupportCommandValidator.cs) | Validates the support end date and requires a closure reason. |
| [`Commands/DeleteSupport/DeleteSupportCommand.cs`](Client.Application/Supports/Commands/DeleteSupport/DeleteSupportCommand.cs) | Locks the support's dossier, soft-deletes the case and looks up the previous support for the same client. |
| [`Commands/SendReferentChangedNotification/Messaging/Config/RabbitMqExchangeSettings.cs`](Client.Application/Supports/Commands/SendReferentChangedNotification/Messaging/Config/RabbitMqExchangeSettings.cs) | Defines RabbitMQ settings for staff synchronization, staff-change routing and annual-closure messaging. |
| [`Commands/SendReferentChangedNotification/Messaging/Dto/IspReferentChangedMessage.cs`](Client.Application/Supports/Commands/SendReferentChangedNotification/Messaging/Dto/IspReferentChangedMessage.cs) | Defines the ISP staff-change payload carrying worker and client identifiers and names. |
| [`Commands/SendReferentChangedNotification/SendSocialWorkerChangedNotificationCommand.cs`](Client.Application/Supports/Commands/SendReferentChangedNotification/SendSocialWorkerChangedNotificationCommand.cs) | Carries the support track ID in a MediatR social-worker-change notification. |
| [`Commands/UpsertSupport/UpsertSupportCommand.cs`](Client.Application/Supports/Commands/UpsertSupport/UpsertSupportCommand.cs) | Creates or updates a support case under dossier and worker locks and persists its staff assignment. |
| [`Commands/UpsertSupport/UpsertSupportCommandValidator.cs`](Client.Application/Supports/Commands/UpsertSupport/UpsertSupportCommandValidator.cs) | Rejects support start dates earlier than existing dates and limits the support note to 255 characters. |
| [`Queries/GetClientsListQuery.cs`](Client.Application/Supports/Queries/GetClientsListQuery.cs) | Projects available support tracks in start-date order for the client list. |
| [`Queries/GetSupportClosureReasonsQuery.cs`](Client.Application/Supports/Queries/GetSupportClosureReasonsQuery.cs) | Reads available support closure reasons from the reference repository. |
| [`Queries/GetSupportsByReferents/GetSupportsBySocialWorkersQuery.cs`](Client.Application/Supports/Queries/GetSupportsByReferents/GetSupportsBySocialWorkersQuery.cs) | Filters, sorts and pages consultant supports using the current authorized membership as the scope. |
| [`Queries/GetSupportsListByClientQuery.cs`](Client.Application/Supports/Queries/GetSupportsListByClientQuery.cs) | Projects and pages supports for the selected client in start-date order. |
| [`Queries/SupportDto.cs`](Client.Application/Supports/Queries/SupportDto.cs) | Carries support dates, client and staff details, status, notes, closure reason and latest-support flag. |
| [`Queries/SupportsListVm.cs`](Client.Application/Supports/Queries/SupportsListVm.cs) | Wraps a collection of support track DTOs. |

## Core

`Client.Core` Models tenant-owned entities, value objects, domain events and repository contracts.

### Project files

| File | Purpose |
| --- | --- |
| [`ClientManagement.Core.csproj`](Client.Core/ClientManagement.Core.csproj) | Defines the .NET 8 domain project. |
| [`packages.lock.json`](Client.Core/packages.lock.json) | Pins the portable .NET 8 Core dependencies and their content hashes. |

### Common

| File | Purpose |
| --- | --- |
| [`AggregateRoot.cs`](Client.Core/Common/AggregateRoot.cs) | Provides the tenant-owned aggregate root for collecting pending domain events. |
| [`DomainEvent.cs`](Client.Core/Common/DomainEvent.cs) | Defines domain event metadata and the contract for entities that carry events. |
| [`Dto/MySupportDto.cs`](Client.Core/Common/Dto/MySupportDto.cs) | Defines the referent's support list projection, including client identity, activity and appointment details. |
| [`Dto/MySupportParameters.cs`](Client.Core/Common/Dto/MySupportParameters.cs) | Defines the query-string parameter type for the current user's support list. |
| [`Entity.cs`](Client.Core/Common/Entity.cs) | Defines shared entity identity, audit and soft-delete fields and organisation ownership. |
| [`Filter.cs`](Client.Core/Common/Filter.cs) | Represents a query filter by property identifier and value. |
| [`Order.cs`](Client.Core/Common/Order.cs) | Represents a sort column and direction. |
| [`QueryStringParameters.cs`](Client.Core/Common/QueryStringParameters.cs) | Provides the base type for query-string sorting. |
| [`User.cs`](Client.Core/Common/User.cs) | Represents user identity, name and soft-delete state. |
| [`ValueObject.cs`](Client.Core/Common/ValueObject.cs) | Implements equality and hashing from a value object's components. |

### Entities

| File | Purpose |
| --- | --- |
| [`ActionChange.cs`](Client.Core/Entities/ActionChange.cs) | Records a follow-up action change with actor, prior and new versions, states, assignment and due dates. |
| [`Assessment.cs`](Client.Core/Entities/Assessment.cs) | Models a client assessment, its completion state and personal, professional and training observations. |
| [`Client.cs`](Client.Core/Entities/Client.cs) | Defines the beneficiary aggregate and its civil identity, contact and reference details. |
| [`ClientAddress.cs`](Client.Core/Entities/ClientAddress.cs) | Adds street, postal code, city and country fields to the partial client entity. |
| [`CurrentAssignment.cs`](Client.Core/Entities/CurrentAssignment.cs) | Defines the predicate for determining whether a social case assignment is active on a calendar date. |
| [`EmploymentTerminationType.cs`](Client.Core/Entities/EmploymentTerminationType.cs) | Models a named employment termination category. |
| [`FollowUpAction.cs`](Client.Core/Entities/FollowUpAction.cs) | Models a tenant-owned follow-up action, its linked interaction, purpose, due date, assignee and state. |
| [`IdempotencyReceipt.cs`](Client.Core/Entities/IdempotencyReceipt.cs) | Records an operation's client key, canonical metadata, affected entity IDs, result version and retention. |
| [`Interaction.cs`](Client.Core/Entities/Interaction.cs) | Models a case interaction with the beneficiary, author, occurrence time, note and revision. |
| [`Language.cs`](Client.Core/Entities/Language.cs) | Models a named language reference. |
| [`MonitoringAction.cs`](Client.Core/Entities/MonitoringAction.cs) | Models a monitoring action reference and its related reports. |
| [`MonitoringReport.cs`](Client.Core/Entities/MonitoringReport.cs) | Models a beneficiary monitoring report with its worker, action, period and comments. |
| [`NatureOfContract.cs`](Client.Core/Entities/NatureOfContract.cs) | Models a named contract nature. |
| [`Profession.cs`](Client.Core/Entities/Profession.cs) | Models a named profession reference. |
| [`ProfessionalAssessment.cs`](Client.Core/Entities/ProfessionalAssessment.cs) | Models profession-specific skills acquired and skills to develop within a client assessment. |
| [`ProfessionnalExperience.cs`](Client.Core/Entities/ProfessionnalExperience.cs) | Models a client's employment experience, contract details and termination context. |
| [`School.cs`](Client.Core/Entities/School.cs) | Models a school with its name and locality. |
| [`SchoolRegistration.cs`](Client.Core/Entities/SchoolRegistration.cs) | Models a client's school and training registration, including dates, level and result. |
| [`SocialCase.cs`](Client.Core/Entities/SocialCase.cs) | Models a beneficiary support case, its assigned social worker, dates and closure reason. |
| [`SocialWorker.cs`](Client.Core/Entities/SocialWorker.cs) | Models the Client-side worker projection and tracks membership, event identity, projection version and team details. |
| [`Training.cs`](Client.Core/Entities/Training.cs) | Models a named training record and its linked training field. |
| [`TrainingField.cs`](Client.Core/Entities/TrainingField.cs) | Models a named training subject area. |
| [`TrainingType.cs`](Client.Core/Entities/TrainingType.cs) | Models a named training type. |

### Enums

| File | Purpose |
| --- | --- |
| [`CategoryOfJob.cs`](Client.Core/Enums/CategoryOfJob.cs) | Defines the domain's job category values. |
| [`CategoryOfPartner.cs`](Client.Core/Enums/CategoryOfPartner.cs) | Defines partner organisation categories. |
| [`CivilStatus.cs`](Client.Core/Enums/CivilStatus.cs) | Defines civil status values used in beneficiary records. |
| [`EmploymentStatus.cs`](Client.Core/Enums/EmploymentStatus.cs) | Distinguishes full-time and part-time employment. |
| [`FieldOfActivity.cs`](Client.Core/Enums/FieldOfActivity.cs) | Defines employment sectors such as trade, construction and maintenance. |
| [`Gender.cs`](Client.Core/Enums/Gender.cs) | Defines the gender values used by the domain. |
| [`Level.cs`](Client.Core/Enums/Level.cs) | Defines education grade and level values. |
| [`PersonalExpectationLanguageknowledgeScore.cs`](Client.Core/Enums/PersonalExpectationLanguageknowledgeScore.cs) | Defines the proficiency levels used to describe language knowledge. |
| [`Reward.cs`](Client.Core/Enums/Reward.cs) | Defines job incentive and reward categories. |
| [`SchoolResult.cs`](Client.Core/Enums/SchoolResult.cs) | Defines training outcome values, including in progress, success, failure and dropout. |
| [`StatusOfJob.cs`](Client.Core/Enums/StatusOfJob.cs) | Defines the job status values, including the all-jobs filter value. |
| [`StatusOfJobOffer.cs`](Client.Core/Enums/StatusOfJobOffer.cs) | Defines open and closed job-offer states. |
| [`StatusOfPartner.cs`](Client.Core/Enums/StatusOfPartner.cs) | Defines active, inactive and stopped partner states. |
| [`TypeOfContract.cs`](Client.Core/Enums/TypeOfContract.cs) | Defines employment contract categories. |
| [`TypeOfCourse.cs`](Client.Core/Enums/TypeOfCourse.cs) | Defines course categories, including language and professional training. |
| [`TypeOfJob.cs`](Client.Core/Enums/TypeOfJob.cs) | Distinguishes new jobs from replacement positions. |

### Exceptions

| File | Purpose |
| --- | --- |
| [`ClientAddressNotFoundException.cs`](Client.Core/Exceptions/ClientAddressNotFoundException.cs) | Defines the domain exception for a missing client address. |
| [`ClientAlreadyExists.cs`](Client.Core/Exceptions/ClientAlreadyExists.cs) | Defines the exception used when a client already exists. |
| [`InvalidAcronymFormatException.cs`](Client.Core/Exceptions/InvalidAcronymFormatException.cs) | Defines the domain exception for an invalid acronym format. |
| [`InvalidEmailException.cs`](Client.Core/Exceptions/InvalidEmailException.cs) | Defines the domain exception for an invalid email address. |
| [`InvalidLanguageNameFormatException.cs`](Client.Core/Exceptions/InvalidLanguageNameFormatException.cs) | Defines the exception used for invalid language-name formatting. |
| [`InvalidNissFormatException.cs`](Client.Core/Exceptions/InvalidNissFormatException.cs) | Defines the domain exception for an invalid NISS identifier format. |
| [`InvalidReferenceNumberFormatException.cs`](Client.Core/Exceptions/InvalidReferenceNumberFormatException.cs) | Defines the domain exception for an invalid client reference number format. |
| [`SeveralSociabiliBeneficiariesFoundedException.cs`](Client.Core/Exceptions/SeveralSociabiliBeneficiariesFoundedException.cs) | Defines the exception for a lookup that finds several matching Sociabili clients. |
| [`StringSizeLimitException.cs`](Client.Core/Exceptions/StringSizeLimitException.cs) | Defines the domain exception for a string outside its permitted size. |

### Interfaces

| File | Purpose |
| --- | --- |
| [`IAssessmentRepository.cs`](Client.Core/Interfaces/IAssessmentRepository.cs) | Defines assessment retrieval, persistence and soft deletion. |
| [`IBeneficiaryRepository.cs`](Client.Core/Interfaces/IBeneficiaryRepository.cs) | Defines `IClientRepository` for client persistence and lookups, including by social security number. |
| [`IBeneficiaryService.cs`](Client.Core/Interfaces/IBeneficiaryService.cs) | Defines the `IClientService` client update and upsert contract. |
| [`ICurrentUserService.cs`](Client.Core/Interfaces/ICurrentUserService.cs) | Defines access to the acting user's identity. |
| [`IDateTime.cs`](Client.Core/Interfaces/IDateTime.cs) | Defines the service clock contract. |
| [`IDocumentRepository.cs`](Client.Core/Interfaces/IDocumentRepository.cs) | Defines the contract for generating a printable assessment document. |
| [`IDomainEventService.cs`](Client.Core/Interfaces/IDomainEventService.cs) | Defines the domain event publication contract. |
| [`IFileService.cs`](Client.Core/Interfaces/IFileService.cs) | Defines the contract for locating, saving, reading and deleting files. |
| [`IFormationRepository.cs`](Client.Core/Interfaces/IFormationRepository.cs) | Defines `ITrainingRepository` for training queries, persistence and soft deletion. |
| [`IGenericReadRepository.cs`](Client.Core/Interfaces/IGenericReadRepository.cs) | Defines a reusable asynchronous read repository. |
| [`ILanguageRepository.cs`](Client.Core/Interfaces/ILanguageRepository.cs) | Defines the language search contract. |
| [`IMonitoringActionRepository.cs`](Client.Core/Interfaces/IMonitoringActionRepository.cs) | Defines monitoring action lookup, persistence and soft deletion. |
| [`IMonitoringReportRepository.cs`](Client.Core/Interfaces/IMonitoringReportRepository.cs) | Defines monitoring report persistence and quarterly lookups by beneficiary or worker. |
| [`INatureOfContractRepository.cs`](Client.Core/Interfaces/INatureOfContractRepository.cs) | Defines contract nature persistence and lookups. |
| [`IProfessionalAssessmentRepository.cs`](Client.Core/Interfaces/IProfessionalAssessmentRepository.cs) | Defines profession-specific assessment persistence and queries by assessment. |
| [`IProfessionnalExperienceRepository.cs`](Client.Core/Interfaces/IProfessionnalExperienceRepository.cs) | Defines work-experience persistence, beneficiary lookup and soft deletion. |
| [`IProfessionRepository.cs`](Client.Core/Interfaces/IProfessionRepository.cs) | Defines profession persistence, listing and uniqueness checks. |
| [`IReferentRepository.cs`](Client.Core/Interfaces/IReferentRepository.cs) | Defines the `ISocialWorkerRepository` contract for worker lookup and persistence. |
| [`IRepositoryBase.cs`](Client.Core/Interfaces/IRepositoryBase.cs) | Defines common repository operations for searching and writing entities. |
| [`IRepositoryManager.cs`](Client.Core/Interfaces/IRepositoryManager.cs) | Collects the repositories and exposes synchronous and asynchronous save operations. |
| [`ISchoolRegistrationRepository.cs`](Client.Core/Interfaces/ISchoolRegistrationRepository.cs) | Defines school registration persistence and queries by registration or beneficiary. |
| [`ISortHelper.cs`](Client.Core/Interfaces/ISortHelper.cs) | Defines the contract for applying requested sort rules to a query. |
| [`ISupportRepository.cs`](Client.Core/Interfaces/ISupportRepository.cs) | Defines support persistence, retrieval and date and beneficiary checks. |
| [`ITrainingFieldRepository.cs`](Client.Core/Interfaces/ITrainingFieldRepository.cs) | Defines training field retrieval, persistence and soft deletion. |
| [`ITrainingTypeRepository.cs`](Client.Core/Interfaces/ITrainingTypeRepository.cs) | Defines training type retrieval, persistence and soft deletion. |

### Lifecycle

| File | Purpose |
| --- | --- |
| [`OrganisationClosureFence.cs`](Client.Core/Lifecycle/OrganisationClosureFence.cs) | Models the Client participant's organisation closure fence, including activation and release. |
| [`OrganisationClosureMessages.cs`](Client.Core/Lifecycle/OrganisationClosureMessages.cs) | Models durable command receipts and outbox messages for organisation closure. |
| [`OrganisationExportFence.cs`](Client.Core/Lifecycle/OrganisationExportFence.cs) | Models the Client participant's organisation export fence, including activation and release. |
| [`OrganisationExportFragment.cs`](Client.Core/Lifecycle/OrganisationExportFragment.cs) | Records an export category snapshot with fence evidence, record counts, schema version and content hash. |
| [`OrganisationExportMessages.cs`](Client.Core/Lifecycle/OrganisationExportMessages.cs) | Models durable command receipts and outbox messages for organisation export. |

### ValueObjects

| File | Purpose |
| --- | --- |
| [`Address.cs`](Client.Core/ValueObjects/Address.cs) | Models an address with street, postal code, city and country and value-based equality. |
| [`Email.cs`](Client.Core/ValueObjects/Email.cs) | Models an email address as a value object. |
| [`Language.cs`](Client.Core/ValueObjects/Language.cs) | Models a spoken language as a value object. |
| [`Niss.cs`](Client.Core/ValueObjects/Niss.cs) | Parses and represents a NISS identifier as a value object. |
| [`Phone.cs`](Client.Core/ValueObjects/Phone.cs) | Models a phone number as a value object. |
| [`PhoneList.cs`](Client.Core/ValueObjects/PhoneList.cs) | Represents an enumerable collection of phone numbers with value-based equality. |
| [`ReasonOfClosure.cs`](Client.Core/ValueObjects/ReasonOfClosure.cs) | Models the reason for closing a support case. |
| [`Reward.cs`](Client.Core/ValueObjects/Reward.cs) | Models a selected reward category as a value object. |

## Infrastructure

`Client.Infrastructure` Implements PostgreSQL persistence, tenant access integration, staff projection messaging, and organisation export and closure handling.

### Project files

| File | Purpose |
| --- | --- |
| [`AdminAreaGrpcService.cs`](Client.Infrastructure/AdminAreaGrpcService.cs) | Fetches a social worker by identifier through AdminArea’s gRPC client. |
| [`ClientManagement.Infrastructure.csproj`](Client.Infrastructure/ClientManagement.Infrastructure.csproj) | Defines the .NET 8 Infrastructure project, its dependencies, linked sources, and embedded v12 RLS manifest. |
| [`DependencyInjection.cs`](Client.Infrastructure/DependencyInjection.cs) | Registers guarded persistence, repositories, external authority clients, and Client data services. |
| [`packages.lock.json`](Client.Infrastructure/packages.lock.json) | Pins the resolved .NET 8 NuGet dependency graph for Client Infrastructure. |
| [`TenantRegistration.cs`](Client.Infrastructure/TenantRegistration.cs) | Registers tenant context, protected worker credentials, and the HTTPS AuthManagement authority client. |

### Authorization

| File | Purpose |
| --- | --- |
| [`ActionAuthorityClient.cs`](Client.Infrastructure/Authorization/ActionAuthorityClient.cs) | Reads organisation time-zone and assignee eligibility decisions from AuthManagement with bounded, strict response validation. |
| [`CurrentTenantAccessClient.cs`](Client.Infrastructure/Authorization/CurrentTenantAccessClient.cs) | Resolves current organisation authority through AuthManagement and permits one bounded credential retry for workers. |
| [`StaffMembershipClient.cs`](Client.Infrastructure/Authorization/StaffMembershipClient.cs) | Verifies a staff member’s organisation membership through AuthManagement. |

### Messaging

| File | Purpose |
| --- | --- |
| [`StaffProjectionChangedV1.cs`](Client.Infrastructure/Messaging/StaffProjectionChangedV1.cs) | Defines the versioned full staff projection message, including removal tombstones. |
| [`StaffProjectionConsumer.cs`](Client.Infrastructure/Messaging/StaffProjectionConsumer.cs) | Processes staff snapshots in tenant-scoped operations, checks live worker authority, and handles duplicate or older deliveries. |

### Migrations

| File | Purpose |
| --- | --- |
| [`20250425093851_Initial Migration.cs`](Client.Infrastructure/Migrations/20250425093851_Initial Migration.cs) | Creates the initial Client dossier, assessment, monitoring, worker, and reference data tables and indexes. |
| [`20250425093851_Initial Migration.Designer.cs`](Client.Infrastructure/Migrations/20250425093851_Initial Migration.Designer.cs) | Records EF Core’s target model for the initial ClientManagement migration. |
| [`20260904124417_OrganisationTenantConstraints.cs`](Client.Infrastructure/Migrations/20260904124417_OrganisationTenantConstraints.cs) | Backfills organisation identifiers and enforces organisation-scoped keys, indexes, and foreign keys. |
| [`20260904124417_OrganisationTenantConstraints.Designer.cs`](Client.Infrastructure/Migrations/20260904124417_OrganisationTenantConstraints.Designer.cs) | Records EF Core’s target model for organisation-scoped relationships. |
| [`20260910165923_ExplicitTenantEnforcement.cs`](Client.Infrastructure/Migrations/20260910165923_ExplicitTenantEnforcement.cs) | Adds explicit assessment-to-client and staff-membership links, including staff projection version and event metadata. |
| [`20260910165923_ExplicitTenantEnforcement.Designer.cs`](Client.Infrastructure/Migrations/20260910165923_ExplicitTenantEnforcement.Designer.cs) | Records EF Core’s target model for explicit Client tenant enforcement. |
| [`20260912180001_PostgreSqlRlsAndRuntimeRoles.cs`](Client.Infrastructure/Migrations/20260912180001_PostgreSqlRlsAndRuntimeRoles.cs) | Installs the organisation context function, row-level security policies, and PostgreSQL runtime-role grants. |
| [`20260928081952_OrganisationExportParticipantV1.cs`](Client.Infrastructure/Migrations/20260928081952_OrganisationExportParticipantV1.cs) | Creates the export fence, fragment, inbox, and outbox tables with RLS and database write guards. |
| [`20260928081952_OrganisationExportParticipantV1.Designer.cs`](Client.Infrastructure/Migrations/20260928081952_OrganisationExportParticipantV1.Designer.cs) | Records EF Core’s target model for the Client lifecycle export participant. |
| [`20260928150226_OrganisationClosureParticipantV1.cs`](Client.Infrastructure/Migrations/20260928150226_OrganisationClosureParticipantV1.cs) | Creates closure fence, inbox, and outbox tables with tenant policies and restricted database release controls. |
| [`20260928150226_OrganisationClosureParticipantV1.Designer.cs`](Client.Infrastructure/Migrations/20260928150226_OrganisationClosureParticipantV1.Designer.cs) | Records EF Core’s target model for the Client organisation closure participant. |
| [`20261013163209_ClientActionWritePrerequisite.cs`](Client.Infrastructure/Migrations/20261013163209_ClientActionWritePrerequisite.cs) | Creates interaction, follow-up action, action history, and idempotency receipt tables with tenant policies and write safeguards. |
| [`20261013163209_ClientActionWritePrerequisite.Designer.cs`](Client.Infrastructure/Migrations/20261013163209_ClientActionWritePrerequisite.Designer.cs) | Records EF Core’s target model for the Client action-write prerequisites. |
| [`ApplicationDbContextModelSnapshot.cs`](Client.Infrastructure/Migrations/ApplicationDbContextModelSnapshot.cs) | Records the current Client EF Core model as the baseline for future migrations. |

### Persistence

| File | Purpose |
| --- | --- |
| [`ActionWriteStore.cs`](Client.Infrastructure/Persistence/ActionWriteStore.cs) | Records interactions and changes follow-up actions with locked dossier checks, current authority checks, and durable idempotency receipts. |
| [`ApplicationDbContext.cs`](Client.Infrastructure/Persistence/ApplicationDbContext.cs) | Defines the Client EF model, guarded tenant runtime context, and separate deployment context. |
| [`AssessmentRepository.cs`](Client.Infrastructure/Persistence/AssessmentRepository.cs) | Looks up current and archived assessments and supports draft checks and soft deletion. |
| [`BeneficiaryB1Store.cs`](Client.Infrastructure/Persistence/BeneficiaryB1Store.cs) | Reads beneficiary context and interactions and records new interactions with durable replay checks. |
| [`ClientParentLock.cs`](Client.Infrastructure/Persistence/ClientParentLock.cs) | Acquires PostgreSQL locks on dossier, action, and worker records and refreshes case state after waiting. |
| [`ClientRepository.cs`](Client.Infrastructure/Persistence/ClientRepository.cs) | Retrieves and searches beneficiary records, including NISS lookups, and persists changes. |
| [`Configurations/ActionWriteConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/ActionWriteConfiguration.cs) | Maps interactions, follow-up actions, change history, and idempotency receipts with tenant-aware relationships and constraints. |
| [`Configurations/AssessmentConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/AssessmentConfiguration.cs) | Maps the organisation-scoped relationship between assessments and beneficiaries. |
| [`Configurations/ClientConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/ClientConfiguration.cs) | Maps beneficiary profile fields, value objects, and a unique identifying index. |
| [`Configurations/ConfigurationKeys.cs`](Client.Infrastructure/Persistence/Configurations/ConfigurationKeys.cs) | Declares configuration section keys for rewards and closure reasons. |
| [`Configurations/LanguageConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/LanguageConfiguration.cs) | Maps language reference data and enforces unique language names. |
| [`Configurations/MonitoringActionConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/MonitoringActionConfiguration.cs) | Maps monitoring action reference records and their identifier column. |
| [`Configurations/MonitoringReportsConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/MonitoringReportsConfiguration.cs) | Maps monitoring reports and their beneficiary, worker, and monitoring action links. |
| [`Configurations/NatureOfContractConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/NatureOfContractConfiguration.cs) | Maps contract type reference data and its primary key. |
| [`Configurations/OrganisationClosureConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/OrganisationClosureConfiguration.cs) | Maps closure fence, inbox, and outbox records with uniqueness and concurrency safeguards. |
| [`Configurations/OrganisationExportConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/OrganisationExportConfiguration.cs) | Maps export fence, fragment, inbox, and outbox records with integrity metadata and uniqueness safeguards. |
| [`Configurations/ProfessionalAssessmentConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/ProfessionalAssessmentConfiguration.cs) | Maps professional assessments to their parent assessment with an organisation-scoped relationship. |
| [`Configurations/ProfessionnalExperience.cs`](Client.Infrastructure/Persistence/Configurations/ProfessionnalExperience.cs) | Maps professional experience records to their parent beneficiary in the same organisation. |
| [`Configurations/SchoolRegistrationConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/SchoolRegistrationConfiguration.cs) | Maps school registration records to their parent beneficiary in the same organisation. |
| [`Configurations/SocialCaseConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/SocialCaseConfiguration.cs) | Maps social cases and their organisation-scoped beneficiary and social worker relationships. |
| [`Configurations/SocialWorkerConfiguration.cs`](Client.Infrastructure/Persistence/Configurations/SocialWorkerConfiguration.cs) | Maps projected social workers, with membership uniqueness and projection revision safeguards. |
| [`DbContextExtensions/DbContextExtension.cs`](Client.Infrastructure/Persistence/DbContextExtensions/DbContextExtension.cs) | Checks whether EF migration history includes every migration in the configured assembly. |
| [`FileService.cs`](Client.Infrastructure/Persistence/FileService.cs) | Saves, reads, and deletes document files under the configured filesystem root. |
| [`GenericReadRepository.cs`](Client.Infrastructure/Persistence/GenericReadRepository.cs) | Exposes an in-memory list through the generic read repository interface. |
| [`Helpers/FileRepositoryHelper.cs`](Client.Infrastructure/Persistence/Helpers/FileRepositoryHelper.cs) | Derives a document folder name from a numeric partner identifier. |
| [`Helpers/StringExtensions.cs`](Client.Infrastructure/Persistence/Helpers/StringExtensions.cs) | Adds substring matching with an explicit comparison mode. |
| [`Helpers/ToStringUtcExtension.cs`](Client.Infrastructure/Persistence/Helpers/ToStringUtcExtension.cs) | Formats a DateTime’s ticks as a representation labeled UTC. |
| [`LanguageRepository.cs`](Client.Infrastructure/Persistence/LanguageRepository.cs) | Searches language reference records by case-insensitive name. |
| [`Lifecycle/ClientClosureFixture.cs`](Client.Infrastructure/Persistence/Lifecycle/ClientClosureFixture.cs) | Restricts Client closure participant registration and commands to one designated fixture organisation. |
| [`Lifecycle/ClientClosureRecoveryCapability.cs`](Client.Infrastructure/Persistence/Lifecycle/ClientClosureRecoveryCapability.cs) | Checks a separate PostgreSQL recovery identity and invokes the restricted closure-fence release function. |
| [`Lifecycle/ClientExportFixture.cs`](Client.Infrastructure/Persistence/Lifecycle/ClientExportFixture.cs) | Restricts export to a designated fixture organisation and writes and verifies deterministic export artifacts. |
| [`Lifecycle/ClientOrganisationClosureParticipant.cs`](Client.Infrastructure/Persistence/Lifecycle/ClientOrganisationClosureParticipant.cs) | Enters and releases Client closure fences with an organisation lock and replay-safe inbox and outbox records. |
| [`Lifecycle/ClientOrganisationExportParticipant.cs`](Client.Infrastructure/Persistence/Lifecycle/ClientOrganisationExportParticipant.cs) | Fences Client activity, materializes deterministic tenant export fragments, and persists delivery and fence release evidence. |
| [`MigrationContextFactory.cs`](Client.Infrastructure/Persistence/MigrationContextFactory.cs) | Supplies the design-time deployment context for migration scaffolding and SQL generation. |
| [`MonitoringActionRepository.cs`](Client.Infrastructure/Persistence/MonitoringActionRepository.cs) | Retrieves and persists monitoring actions and supports soft deletion. |
| [`MonitoringReportRepository.cs`](Client.Infrastructure/Persistence/MonitoringReportRepository.cs) | Queries monitoring reports by beneficiary or staff member and persists updates and soft deletes. |
| [`NatureOfContractRepository.cs`](Client.Infrastructure/Persistence/NatureOfContractRepository.cs) | Queries, persists, and soft-deletes contract type reference records. |
| [`ProfessionBilansRepository.cs`](Client.Infrastructure/Persistence/ProfessionBilansRepository.cs) | Queries, persists, and soft-deletes professional assessments and retrieves their profession information. |
| [`ProfessionnalExperienceRepository.cs`](Client.Infrastructure/Persistence/ProfessionnalExperienceRepository.cs) | Queries, persists, and soft-deletes beneficiary professional experience records. |
| [`RepositoryBase.cs`](Client.Infrastructure/Persistence/RepositoryBase.cs) | Provides shared EF Core CRUD operations and configurable query tracking. |
| [`RepositoryManager.cs`](Client.Infrastructure/Persistence/RepositoryManager.cs) | Provides the service’s repositories over a shared context and saves their pending changes. |
| [`SchoolRegistrationRepository.cs`](Client.Infrastructure/Persistence/SchoolRegistrationRepository.cs) | Queries school registrations by beneficiary and supports persistence and soft deletion. |
| [`SocialWorkerRepository.cs`](Client.Infrastructure/Persistence/SocialWorkerRepository.cs) | Looks up and persists projected social worker records. |
| [`SupportRepository.cs`](Client.Infrastructure/Persistence/SupportRepository.cs) | Queries and persists social cases, including assigned-worker views and date consistency checks. |
| [`TenantTransactionAttempt.cs`](Client.Infrastructure/Persistence/TenantTransactionAttempt.cs) | Establishes guarded PostgreSQL tenant transactions and prevents replay of an authority decision after a failed attempt. |

### Security

| File | Purpose |
| --- | --- |
| [`rls-manifest.v11.json`](Client.Infrastructure/Security/rls-manifest.v11.json) | Declares the Client PostgreSQL security contract through the organisation closure migration. |
| [`rls-manifest.v12.json`](Client.Infrastructure/Security/rls-manifest.v12.json) | Declares the Client PostgreSQL security contract extended to interaction, action, history, and idempotency tables. |

### Services

| File | Purpose |
| --- | --- |
| [`ClientService.cs`](Client.Infrastructure/Services/ClientService.cs) | Provides the current placeholder implementation of the Client update and upsert interface. |
| [`DateTimeService.cs`](Client.Infrastructure/Services/DateTimeService.cs) | Exposes local system time through the application clock interface. |

## Tests

`Tests` Covers domain and application behavior, tenant authorization, persistence, migrations and messaging integration.

### Application.IntegrationTests

| File | Purpose |
| --- | --- |
| [`Application.IntegrationTests.csproj`](Tests/Application.IntegrationTests/Application.IntegrationTests.csproj) | Defines the .NET 8 xUnit application integration suite, referencing the Client API and infrastructure, PostgreSQL Testcontainers, and shared fixture sources. |
| [`ClientApiContractTests.cs`](Tests/Application.IntegrationTests/ClientApiContractTests.cs) | Exercises body-only Client search and native-language PATCH, requiring authentication and no caching and checking that legacy routes and sensitive-value error echoes are absent. |
| [`GatewayContractTests.cs`](Tests/Application.IntegrationTests/GatewayContractTests.cs) | Checks that Gateway forwards each sensitive Client route only by its agreed HTTP method and without caching. |
| [`GlobalUsings.cs`](Tests/Application.IntegrationTests/GlobalUsings.cs) | Imports xUnit globally for the application integration suite. |
| [`NissPersistenceTests.cs`](Tests/Application.IntegrationTests/NissPersistenceTests.cs) | Exercises PostgreSQL round-trip and update of canonical NISS values and rejects invalid legacy values during materialization. |
| [`packages.lock.json`](Tests/Application.IntegrationTests/packages.lock.json) | Pins the application integration suite’s NuGet dependency graph for locked restores. |
| [`TenantPersistenceMigrationTests.cs`](Tests/Application.IntegrationTests/TenantPersistenceMigrationTests.cs) | Exercises fresh tenant migration, failed legacy backfill and safe retry, and verifies EF uses the shared service connection. |

### Application.UnitTests

| File | Purpose |
| --- | --- |
| [`Application.UnitTests.csproj`](Tests/Application.UnitTests/Application.UnitTests.csproj) | Defines the .NET 8 xUnit application unit suite and references Client Application and Core. |
| [`AuthorityGrantContractTests.cs`](Tests/Application.UnitTests/AuthorityGrantContractTests.cs) | Checks live grant parsing, fresh authority rechecks, stable membership binding, denial of malformed decisions and safe operational event fields. |
| [`ClosureGateRoutingTests.cs`](Tests/Application.UnitTests/ClosureGateRoutingTests.cs) | Classifies every command handler’s activity and checks that organisation writes use the ordinary transaction path without replaying action writes. |
| [`GlobalUsings.cs`](Tests/Application.UnitTests/GlobalUsings.cs) | Imports xUnit globally for the application unit suite. |
| [`MySupportsAuthorizationTests.cs`](Tests/Application.UnitTests/MySupportsAuthorizationTests.cs) | Checks that MySupports uses the live membership for assigned and all scope and denies missing linkage before persistence. |
| [`NissCommandTests.cs`](Tests/Application.UnitTests/NissCommandTests.cs) | Checks that create, update and patch reject noncanonical NISS input before consulting the repository. |
| [`packages.lock.json`](Tests/Application.UnitTests/packages.lock.json) | Pins the application unit suite’s NuGet dependency graph for locked restores. |
| [`SensitiveRequestLoggingTests.cs`](Tests/Application.UnitTests/SensitiveRequestLoggingTests.cs) | Checks that ordinary, slow and exception request logs omit NISS, search payloads and exception messages. |
| [`TenantAuthorizationTests.cs`](Tests/Application.UnitTests/TenantAuthorizationTests.cs) | Checks explicit request permissions, forged or absent tenant authority, assigned scope, platform permission boundaries and isolation across parallel operation types. |

### Common

| File | Purpose |
| --- | --- |
| [`SyntheticClient.cs`](Tests/Common/SyntheticClient.cs) | Builds synthetic Client entities and generates NISS values for test setup. |

### Domain.UnitTests

| File | Purpose |
| --- | --- |
| [`ClientTest.cs`](Tests/Domain.UnitTests/ClientTest.cs) | Checks Client constructor validation, full-name composition and initialized case and school registration collections. |
| [`Domain.UnitTests.csproj`](Tests/Domain.UnitTests/Domain.UnitTests.csproj) | Defines the .NET 8 xUnit domain unit suite and references Client Core. |
| [`GlobalUsings.cs`](Tests/Domain.UnitTests/GlobalUsings.cs) | Imports xUnit globally for the domain unit suite. |
| [`NissTests.cs`](Tests/Domain.UnitTests/NissTests.cs) | Checks NISS parsing, independent checksum vectors, canonical reassignment and rejection of invalid values without exposing them. |
| [`packages.lock.json`](Tests/Domain.UnitTests/packages.lock.json) | Pins the domain unit suite’s NuGet dependency graph for locked restores. |
| [`TenantOwnedEntityTests.cs`](Tests/Domain.UnitTests/TenantOwnedEntityTests.cs) | Checks that the organisation identifier is immutable once assigned. |
| [`TrainingFieldTests.cs`](Tests/Domain.UnitTests/TrainingFieldTests.cs) | Checks that a training field retains a valid name and rejects a missing name. |

### Infrastructure.IntegrationTests

| File | Purpose |
| --- | --- |
| [`AccessResilienceTests.cs`](Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs) | Exercises Auth adapter failure translation, one safe retry without caching, shared deadlines, caller cancellation and identity-free metrics and traces. |
| [`AssignedClientTests.cs`](Tests/Infrastructure.IntegrationTests/AssignedClientTests.cs) | Exercises live assignment checks for lists, details and detached writes, and round-trips changes to flattened optional values. |
| [`AuthenticationStartupTests.cs`](Tests/Infrastructure.IntegrationTests/AuthenticationStartupTests.cs) | Runs inherited real-host authentication startup tests with disposable PostgreSQL, RabbitMQ and Client containers, checking that the staff projection queue has a consumer. |
| [`AuthorityCorrectionTests.cs`](Tests/Infrastructure.IntegrationTests/AuthorityCorrectionTests.cs) | Exercises named dependency 503 responses, fail-closed worker and middleware behavior, correlation and independence of logging and activity events. |
| [`AuthorityResponseContractTests.cs`](Tests/Infrastructure.IntegrationTests/AuthorityResponseContractTests.cs) | Checks Auth response integrity and schema, decision classification, bounded dependency retry, recognized grants and fresh authority after denial or success. |
| [`BearerValidationTests.cs`](Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs) | Binds the shared bearer validator contract tests to Client’s actual tenant authentication registration. |
| [`BeneficiaryB1TestApi.cs`](Tests/Infrastructure.IntegrationTests/BeneficiaryB1TestApi.cs) | Provides the test-only B1 interaction controller and problem middleware for recording and reading beneficiary interactions and revisions. |
| [`BeneficiaryLookupCancellationTests.cs`](Tests/Infrastructure.IntegrationTests/BeneficiaryLookupCancellationTests.cs) | Checks that canceling beneficiary lookup stops the in-flight PostgreSQL query without returning a result. |
| [`BeneficiaryLookupHttpTests.cs`](Tests/Infrastructure.IntegrationTests/BeneficiaryLookupHttpTests.cs) | Exercises authorized lookup order, caps and pagination, live permission changes, non-disclosing failures, safe logs and PostgreSQL RLS. |
| [`CapturedOwnerResponseTests.cs`](Tests/Infrastructure.IntegrationTests/CapturedOwnerResponseTests.cs) | Checks that the captured owner response accepts known grants without treating unknown extras as authority. |
| [`ClientOrganisationClosureParticipantEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/ClientOrganisationClosureParticipantEvidenceTests.cs) | Exercises durable organisation closure, canonical receipt replay, tenant isolation, closure triggers, concurrent transaction ordering and denial of later writes. |
| [`ClientOrganisationExportParticipantEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/ClientOrganisationExportParticipantEvidenceTests.cs) | Exercises deterministic and tenant-isolated export, write fencing, artifact tamper rejection, immutable receipt replay and fenced release. |
| [`CurrentAssignmentTests.cs`](Tests/Infrastructure.IntegrationTests/CurrentAssignmentTests.cs) | Exercises current assignment across support reads and edits, authorized membership for MySupports, rollback and rejection of cross-organisation worker links. |
| [`DossierLookupHttpTests.cs`](Tests/Infrastructure.IntegrationTests/DossierLookupHttpTests.cs) | Exercises the dossier HTTP response schema and keeps hidden or unassigned dossiers indistinguishable from missing, while preserving explicit authorization failures. |
| [`FirstSqlPathEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/FirstSqlPathEvidenceTests.cs) | Observes first-SQL ordering on the restricted PostgreSQL runtime role for Client HTTP and consumer paths and checks Supports authorization through MediatR. |
| [`Infrastructure.IntegrationTests.csproj`](Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj) | Defines the .NET 8 Client infrastructure integration suite with the Client API, infrastructure, ASP.NET Core test hosting and PostgreSQL Testcontainers. |
| [`Issue76ActionWriteTests.cs`](Tests/Infrastructure.IntegrationTests/Issue76ActionWriteTests.cs) | Exercises atomic and idempotent action writes, live authority and assignment, stale observations, lock contention, rollback and ambiguous-commit reconciliation. |
| [`Issue81AuthorityHttpTests.cs`](Tests/Infrastructure.IntegrationTests/Issue81AuthorityHttpTests.cs) | Exercises exact eligibility echoes, service authentication, transient retry, deadlines and fail-closed Brussels calendar configuration. |
| [`Issue83BeneficiaryB1Tests.cs`](Tests/Infrastructure.IntegrationTests/Issue83BeneficiaryB1Tests.cs) | Exercises B1 interaction writes and reads through a test host, including tenant scope, receipt replay, input bounds, concurrent closure and ambiguous commit. |
| [`LocalStackHealthTests.cs`](Tests/Infrastructure.IntegrationTests/LocalStackHealthTests.cs) | Checks LocalStack liveness and readiness semantics, sanitized failures and continued authentication on neighboring business routes. |
| [`MappingCharacterizationTests.cs`](Tests/Infrastructure.IntegrationTests/MappingCharacterizationTests.cs) | Characterizes Client read projections and tenant visibility, records SQL and materialization evidence, and checks the dormant IBIS patch path. |
| [`MembershipMigrationTests.cs`](Tests/Infrastructure.IntegrationTests/MembershipMigrationTests.cs) | Checks that membership backfill requires an unambiguous reviewed mapping and retains existing rows. |
| [`packages.lock.json`](Tests/Infrastructure.IntegrationTests/packages.lock.json) | Pins the infrastructure integration suite’s NuGet dependency graph for locked restores. |
| [`PostgreSqlRoleCatalogEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlRoleCatalogEvidenceTests.cs) | Migrates a fresh PostgreSQL instance through owner-role assumption and compares the Client role catalog to the security manifest, including privilege denials. |
| [`RuntimeConnectionConfigurationTests.cs`](Tests/Infrastructure.IntegrationTests/RuntimeConnectionConfigurationTests.cs) | Checks that the Client runtime database connection must be configured with a nonblank value. |
| [`StaffProjectionTests.cs`](Tests/Infrastructure.IntegrationTests/StaffProjectionTests.cs) | Checks that staff projection consumers re-authorize each scope and apply versioned membership snapshots idempotently. |
| [`TenantEnforcementTests.cs`](Tests/Infrastructure.IntegrationTests/TenantEnforcementTests.cs) | Exercises EF tenant classifications and filters, fail-closed missing context, forged and detached writes, pooled connection isolation and audited platform access. |
| [`TenantHttpTests.cs`](Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs) | Checks JWT validity, HTTP metadata and failure translation, conflicting tenant selections, exact adapter response handling and the total retry deadline. |
| [`WorkerAuthorizationRetryTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerAuthorizationRetryTests.cs) | Exercises one worker credential reload after a 401 within the shared budget, including cancellation, concurrent scopes, stale credential rejection and consumer failure behavior. |
| [`WorkerCredentialBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerCredentialBoundaryTests.cs) | Checks that missing credentials, out-of-scope delivery and tenant denial prevent database work or credential adoption. |
