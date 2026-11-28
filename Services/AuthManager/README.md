# AuthManager

AuthManager is the Zeka backend service for identity, browser authentication and organisation access. It handles account registration and email verification, issues access tokens and publishes the public JWKS, and manages organisation provisioning, tenant membership, permissions and worker credential rotation. It also implements Auth operations for organisation export, closure and related lifecycle workflows.

The file reference is grouped by project and component. See the [migration README](AuthManager.Infrastructure/Migrations/README.md) for database migration details.

[API](#api) · [Application](#application) · [Core](#core) · [Infrastructure](#infrastructure) · [Tests](#tests)

## API

`AuthManager.API` Hosts the ASP.NET Core endpoints for browser sessions and onboarding, token issuer discovery, tenant access and internal service calls.

### Project files

| File | Purpose |
| --- | --- |
| [`appsettings.Development.json`](AuthManager.API/appsettings.Development.json) | Provides logging settings for local development. |
| [`appsettings.json`](AuthManager.API/appsettings.json) | Defines base logging, allowed hosts, connection-string and outbox configuration sections. |
| [`AssigneeServiceAuthentication.cs`](AuthManager.API/AssigneeServiceAuthentication.cs) | Configures the dedicated assignee-service bearer scheme, trusted token issuer and JWKS lookup. |
| [`AuthManagement.http`](AuthManager.API/AuthManagement.http) | Contains the ASP.NET starter weather-forecast HTTP request. |
| [`AuthManager.API.csproj`](AuthManager.API/AuthManager.API.csproj) | Defines the .NET 8 web host and its Application, Infrastructure and shared authentication project references. |
| [`Dockerfile`](AuthManager.API/Dockerfile) | Performs the strict locked restore, builds and publishes the host, and packages it in a non-root ASP.NET Core runtime image. |
| [`packages.lock.json`](AuthManager.API/packages.lock.json) | Pins API NuGet dependencies and their content hashes for locked restore. |
| [`Program.cs`](AuthManager.API/Program.cs) | Composes the AuthManager host, registers authentication and routes, and maps LocalStack health checks with issuer readiness. |
| [`TenantAuthentication.cs`](AuthManager.API/TenantAuthentication.cs) | Registers the shared Zeka bearer-token validation service for the AuthManager API. |

### Endpoints

| File | Purpose |
| --- | --- |
| [`AssigneeEligibilityEndpoint.cs`](AuthManager.API/Endpoints/AssigneeEligibilityEndpoint.cs) | Exposes the internal eligibility check for assignees and records request outcomes and latency. |
| [`AuthApiEndpoints.cs`](AuthManager.API/Endpoints/AuthApiEndpoints.cs) | Composes the authentication routes, the retired registration response and the Auth HTTPS redirection policy. |
| [`BrowserEligibleOrganisationsEndpoint.cs`](AuthManager.API/Endpoints/BrowserEligibleOrganisationsEndpoint.cs) | Lists the organisations a signed-in browser user is eligible to select. |
| [`BrowserOrganisationEndpoints.cs`](AuthManager.API/Endpoints/BrowserOrganisationEndpoints.cs) | Handles browser requests to create an organisation or check access to a selected organisation. |
| [`BrowserRegistrationEndpoints.cs`](AuthManager.API/Endpoints/BrowserRegistrationEndpoints.cs) | Handles registration, verification email resend and verification-token exchange with request and abuse limits. |
| [`BrowserSessionEndpoints.cs`](AuthManager.API/Endpoints/BrowserSessionEndpoints.cs) | Handles CSRF token requests, sign-in, session restore and logout, including cookie handling and trusted caller attribution. |
| [`ConfigureTimeZoneIngress.cs`](AuthManager.API/Endpoints/ConfigureTimeZoneIngress.cs) | Maps the disabled internal time-zone configuration route. |
| [`IssuerEndpoints.cs`](AuthManager.API/Endpoints/IssuerEndpoints.cs) | Publishes the issuer's public JWKS and maps the authentication health endpoint. |
| [`OrganisationTimeZoneEndpoint.cs`](AuthManager.API/Endpoints/OrganisationTimeZoneEndpoint.cs) | Exposes the internal organisation time-zone lookup. |
| [`TenantAccessEndpoint.cs`](AuthManager.API/Endpoints/TenantAccessEndpoint.cs) | Serves current tenant access and individual membership lookups for authenticated callers. |
| [`WorkerRotationEndpoints.cs`](AuthManager.API/Endpoints/WorkerRotationEndpoints.cs) | Exposes internal M1 endpoints to prepare worker credential rotation and revoke superseded credentials. |

### Filters

| File | Purpose |
| --- | --- |
| [`ValidationFilter.cs`](AuthManager.API/Filters/ValidationFilter.cs) | Validates endpoint inputs before dispatching an application request. |

### Properties

| File | Purpose |
| --- | --- |
| [`launchSettings.json`](AuthManager.API/Properties/launchSettings.json) | Defines local development and IDE launch profiles. |

## Application

`AuthManager.Application` Defines the authentication, authorization, provisioning and lifecycle use cases and their persistence and delivery contracts.

### Project files

| File | Purpose |
| --- | --- |
| [`AuthManager.Application.csproj`](AuthManager.Application/AuthManager.Application.csproj) | Defines the .NET 8 application project and its domain and lifecycle-contract references. |
| [`DependencyInjection.cs`](AuthManager.Application/DependencyInjection.cs) | Registers the application layer's services with the dependency injection container. |
| [`packages.linux-x64.lock.json`](AuthManager.Application/packages.linux-x64.lock.json) | Pins application package resolution for the Linux x64 runtime. |
| [`packages.lock.json`](AuthManager.Application/packages.lock.json) | Pins the application's portable .NET 8 NuGet dependency graph and package hashes. |

### Authentication

| File | Purpose |
| --- | --- |
| [`BrowserSessions.cs`](AuthManager.Application/Authentication/BrowserSessions.cs) | Defines browser session operations and results, plus the contract for issuing session access tokens. |
| [`IEmailVerificationDelivery.cs`](AuthManager.Application/Authentication/IEmailVerificationDelivery.cs) | Defines verification email delivery and its categorized failure contract. |
| [`Models/RegisterUserRequest.cs`](AuthManager.Application/Authentication/Models/RegisterUserRequest.cs) | Defines the input model for the application user-registration contract. |
| [`Models/UserRegistrationResult.cs`](AuthManager.Application/Authentication/Models/UserRegistrationResult.cs) | Defines success and failure results for the user-registration contract. |
| [`Validators/Users/RegisterUserRequestValidator.cs`](AuthManager.Application/Authentication/Validators/Users/RegisterUserRequestValidator.cs) | Defines FluentValidation rules for the registration request model. |

### Authorization

| File | Purpose |
| --- | --- |
| [`AssigneeEligibility.cs`](AuthManager.Application/Authorization/AssigneeEligibility.cs) | Defines the request, decision and service contract for checking assignee eligibility. |
| [`CurrentTenantAccess.cs`](AuthManager.Application/Authorization/CurrentTenantAccess.cs) | Defines the current-tenant-access service, outcomes and denied-result shape. |
| [`IActiveOrganisationMembership.cs`](AuthManager.Application/Authorization/IActiveOrganisationMembership.cs) | Defines the contract for checking an active organisation membership. |
| [`IEligibleOrganisations.cs`](AuthManager.Application/Authorization/IEligibleOrganisations.cs) | Defines the eligible-organisation lookup and result models. |
| [`MembershipPermissionGrants.cs`](AuthManager.Application/Authorization/MembershipPermissionGrants.cs) | Coordinates explicit membership permission grants and revocations through a persistence contract. |
| [`OrganisationTimeZone.cs`](AuthManager.Application/Authorization/OrganisationTimeZone.cs) | Defines the request, decision and service contract for organisation time-zone lookup. |

### Common

| File | Purpose |
| --- | --- |
| [`Auditing/AuditEntryRequest.cs`](AuthManager.Application/Common/Auditing/AuditEntryRequest.cs) | Defines the data supplied when recording an audit entry. |
| [`Auditing/IAuditWriter.cs`](AuthManager.Application/Common/Auditing/IAuditWriter.cs) | Defines the application contract for writing audit entries. |
| [`Idempotency/IdempotencyDecision.cs`](AuthManager.Application/Common/Idempotency/IdempotencyDecision.cs) | Defines the decision values returned by idempotency checks. |
| [`Idempotency/IIdempotencyStore.cs`](AuthManager.Application/Common/Idempotency/IIdempotencyStore.cs) | Defines the storage contract for recording and resolving idempotent operations. |
| [`Interfaces/IAuthenticationService.cs`](AuthManager.Application/Common/Interfaces/IAuthenticationService.cs) | Defines the application authentication service contract. |
| [`Interfaces/IServiceManager.cs`](AuthManager.Application/Common/Interfaces/IServiceManager.cs) | Defines access to the application authentication service through the service manager. |
| [`Outbox/OutboxContracts.cs`](AuthManager.Application/Common/Outbox/OutboxContracts.cs) | Defines outbox requests, envelopes and interfaces for writing, publishing and dispatching messages. |
| [`Services/ServiceManager.cs`](AuthManager.Application/Common/Services/ServiceManager.cs) | Implements the service manager and exposes the registered authentication service. |

### Lifecycle

| File | Purpose |
| --- | --- |
| [`LifecycleAdmission.cs`](AuthManager.Application/Lifecycle/LifecycleAdmission.cs) | Evaluates lifecycle requests against permissions and reviewed registry data before admitting an operation. |
| [`LifecycleClosureCoordinator.cs`](AuthManager.Application/Lifecycle/LifecycleClosureCoordinator.cs) | Coordinates closure progress, participant receipts and recovery before and after the archive boundary. |
| [`LifecycleDisposition.cs`](AuthManager.Application/Lifecycle/LifecycleDisposition.cs) | Evaluates retention policy and records retained exceptions and purge eligibility for lifecycle categories. |
| [`LifecycleExportCoordinator.cs`](AuthManager.Application/Lifecycle/LifecycleExportCoordinator.cs) | Coordinates lifecycle export participants and recovery, and defines inventory, packaging and artifact contracts. |
| [`LifecyclePurge.cs`](AuthManager.Application/Lifecycle/LifecyclePurge.cs) | Defines and coordinates non-production purge admission, participant messages and receipt recording. |
| [`LifecycleVerification.cs`](AuthManager.Application/Lifecycle/LifecycleVerification.cs) | Coordinates non-production purge verification, including observations, re-observation and finalization. |

### Onboarding

| File | Purpose |
| --- | --- |
| [`RegistrationContracts.cs`](AuthManager.Application/Onboarding/RegistrationContracts.cs) | Defines the browser onboarding registration, resend and token exchange requests, outcomes and service contract. |

### Provisioning

| File | Purpose |
| --- | --- |
| [`BrowserOrganisationCreation.cs`](AuthManager.Application/Provisioning/BrowserOrganisationCreation.cs) | Defines the browser organisation creation contract and its result codes. |
| [`OrganisationProvisioning.cs`](AuthManager.Application/Provisioning/OrganisationProvisioning.cs) | Implements organisation provisioning and defines its ownership policy, idempotency, audit and event contracts. |

### Workers

| File | Purpose |
| --- | --- |
| [`WorkerRotation.cs`](AuthManager.Application/Workers/WorkerRotation.cs) | Coordinates M1 worker credential rotation and defines worker profiles, request types and delivery receipts. |

## Core

`AuthManager.Core` Models organisations, memberships, permissions and lifecycle operations and evidence.

### Project files

| File | Purpose |
| --- | --- |
| [`AuthManager.Core.csproj`](AuthManager.Core/AuthManager.Core.csproj) | Defines the .NET 8 domain project. |
| [`packages.linux-x64.lock.json`](AuthManager.Core/packages.linux-x64.lock.json) | Pins Core package resolution for the Linux x64 runtime. |
| [`packages.lock.json`](AuthManager.Core/packages.lock.json) | Pins Core's portable .NET 8 NuGet dependency graph and package hashes. |

### Enums

| File | Purpose |
| --- | --- |
| [`UserStatus.cs`](AuthManager.Core/Enums/UserStatus.cs) | Defines the domain's user account status values. |

### Lifecycle

| File | Purpose |
| --- | --- |
| [`LifecycleClosureRecords.cs`](AuthManager.Core/Lifecycle/LifecycleClosureRecords.cs) | Models closure fence receipts and Auth closure participant state, executions and inbox/outbox records. |
| [`LifecycleExportInventory.cs`](AuthManager.Core/Lifecycle/LifecycleExportInventory.cs) | Defines export category requirements and hashes the inventory tied to a reviewed registry revision. |
| [`LifecycleExportRecords.cs`](AuthManager.Core/Lifecycle/LifecycleExportRecords.cs) | Models lifecycle export receipts, fragments, packages, coordinator leases and Auth participant records. |
| [`LifecycleOperation.cs`](AuthManager.Core/Lifecycle/LifecycleOperation.cs) | Models lifecycle operations and participants, including revisions, transitions and closure-boundary evidence. |
| [`LifecyclePurgeProtocol.cs`](AuthManager.Core/Lifecycle/LifecyclePurgeProtocol.cs) | Defines the frozen purge plan, participant message and receipt protocol, and purge progress records. |
| [`LifecycleRegistry.cs`](AuthManager.Core/Lifecycle/LifecycleRegistry.cs) | Defines the reviewed participant registry, its capabilities and validation rules. |
| [`LifecycleRetentionRecords.cs`](AuthManager.Core/Lifecycle/LifecycleRetentionRecords.cs) | Models retention decisions and the versioned, hashed decision sets that contain them. |
| [`LifecycleVerificationEvidence.cs`](AuthManager.Core/Lifecycle/LifecycleVerificationEvidence.cs) | Models verification commands, observed evidence and their operation and participant identities. |
| [`ReviewedClosureRegistryV1.cs`](AuthManager.Core/Lifecycle/ReviewedClosureRegistryV1.cs) | Defines the reviewed registry revision and participant bindings for closure and export. |
| [`ReviewedDispositionRegistryV1.cs`](AuthManager.Core/Lifecycle/ReviewedDispositionRegistryV1.cs) | Defines the reviewed disposition registry and checks that the frozen category inventory is complete. |

### Organisations

| File | Purpose |
| --- | --- |
| [`MembershipPermissionGrant.cs`](AuthManager.Core/Organisations/MembershipPermissionGrant.cs) | Models an organisation membership permission grant and its grant, revocation and concurrency state. |
| [`MembershipStatus.cs`](AuthManager.Core/Organisations/MembershipStatus.cs) | Defines the lifecycle status values of an organisation membership. |
| [`Organisation.cs`](AuthManager.Core/Organisations/Organisation.cs) | Models organisations, owners, time-zone settings, concurrency and lifecycle transitions. |
| [`OrganisationMembership.cs`](AuthManager.Core/Organisations/OrganisationMembership.cs) | Models a user's organisation membership, permission set, and join, suspend, reactivate and leave transitions. |
| [`OrganisationStatus.cs`](AuthManager.Core/Organisations/OrganisationStatus.cs) | Defines organisation lifecycle status values. |
| [`PermissionSet.cs`](AuthManager.Core/Organisations/PermissionSet.cs) | Defines stable system permission sets, including the owner and staff projection worker sets. |
| [`TenantPermissions.cs`](AuthManager.Core/Organisations/TenantPermissions.cs) | Resolves recognised tenant permissions and checks which permissions can be explicitly granted. |

## Infrastructure

`AuthManager.Infrastructure` Implements token issuance, identity and session storage, verification email, database migrations and persistence, and lifecycle and outbox adapters.

### Project files

| File | Purpose |
| --- | --- |
| [`AuthManager.Infrastructure.csproj`](AuthManager.Infrastructure/AuthManager.Infrastructure.csproj) | Builds the .NET 8 infrastructure assembly with Identity, PostgreSQL, time-zone, shared authentication and lifecycle dependencies. |
| [`DependencyInjection.cs`](AuthManager.Infrastructure/DependencyInjection.cs) | Registers Auth persistence, Identity adapters, onboarding and organisation creation services, and readiness and outbox workers. |
| [`packages.linux-x64.lock.json`](AuthManager.Infrastructure/packages.linux-x64.lock.json) | Pins Auth infrastructure NuGet dependencies for .NET 8 and Linux x64. |
| [`packages.lock.json`](AuthManager.Infrastructure/packages.lock.json) | Pins the resolved .NET 8 Auth infrastructure dependencies for locked restore. |

### Authentication

| File | Purpose |
| --- | --- |
| [`AccessTokenIssuer.cs`](AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs) | Issues signed access tokens and browser-session JWTs only while the active RSA key matches the confirmed public JWKS. |
| [`IssuerOptions.cs`](AuthManager.Infrastructure/Authentication/IssuerOptions.cs) | Loads and validates the active signing key reference, token lifetime and retiring public verification keys. |
| [`IssuerRegistration.cs`](AuthManager.Infrastructure/Authentication/IssuerRegistration.cs) | Registers the file-backed token issuer and exposes the same instance for access tokens, session tokens and JWKS publication. |
| [`SigningKeyProvider.cs`](AuthManager.Infrastructure/Authentication/SigningKeyProvider.cs) | Opens bounded PEM files as RSA signing or public verification keys and rejects invalid or undersized keys. |

### CustomTokenProviders

| File | Purpose |
| --- | --- |
| [`EmailConfirmationTokenProvider.cs`](AuthManager.Infrastructure/CustomTokenProviders/EmailConfirmationTokenProvider.cs) | Adapts the ASP.NET Core Identity data-protection token provider for email confirmation. |

### Identity

| File | Purpose |
| --- | --- |
| [`ActiveOrganisationMembership.cs`](AuthManager.Infrastructure/Identity/ActiveOrganisationMembership.cs) | Checks whether a membership belongs to the organisation and optionally requires it to be active. |
| [`AssigneeEligibilityResolver.cs`](AuthManager.Infrastructure/Identity/AssigneeEligibilityResolver.cs) | Reads current tenant membership and grants in a bounded database observation to determine assignee eligibility. |
| [`BrowserSessionPolicy.cs`](AuthManager.Infrastructure/Identity/BrowserSessionPolicy.cs) | Validates host-supplied browser access, idle and absolute lifetimes and the refresh-credential hash-key file reference. |
| [`BrowserSessions.cs`](AuthManager.Infrastructure/Identity/BrowserSessions.cs) | Authenticates browser sign-ins and manages refresh rotation, session expiry, revocation, logout and current-session checks. |
| [`CurrentTenantAccessResolver.cs`](AuthManager.Infrastructure/Identity/CurrentTenantAccessResolver.cs) | Resolves current tenant access from a consistent database view of the user, organisation, active membership, role and explicit grants. |
| [`EligibleOrganisationsResolver.cs`](AuthManager.Infrastructure/Identity/EligibleOrganisationsResolver.cs) | Lists active organisations available to a confirmed user based on their memberships in a single database snapshot. |
| [`EmailVerification/GmailEmailVerificationDelivery.cs`](AuthManager.Infrastructure/Identity/EmailVerification/GmailEmailVerificationDelivery.cs) | Delivers a verification email to the approved recipient through Gmail STARTTLS after validating the landing URL and protected app-password file. |
| [`IdentityAuthenticationService.cs`](AuthManager.Infrastructure/Identity/IdentityAuthenticationService.cs) | Uses the ASP.NET Core Identity user manager to create a registered user and maps the creation result. |
| [`Models/User.cs`](AuthManager.Infrastructure/Identity/Models/User.cs) | Models the GUID-based Identity user and guards activation of pending users until email confirmation is recorded. |
| [`OrganisationTimeZoneResolver.cs`](AuthManager.Infrastructure/Identity/OrganisationTimeZoneResolver.cs) | Reads the active organisation's configured time zone and checks the actor's eligibility in a current tenant-scoped observation. |

### Lifecycle

| File | Purpose |
| --- | --- |
| [`AuthClosureParticipant.cs`](AuthManager.Infrastructure/Lifecycle/AuthClosureParticipant.cs) | Processes organisation closure fence commands with durable inbox and acknowledgment records under the organisation transaction lock. |
| [`AuthClosureRecoveryCapability.cs`](AuthManager.Infrastructure/Lifecycle/AuthClosureRecoveryCapability.cs) | Releases a closure fence using the dedicated PostgreSQL recovery capability after checking its connection identity. |
| [`AuthExportParticipant.cs`](AuthManager.Infrastructure/Lifecycle/AuthExportParticipant.cs) | Enters and releases export fences and stages the Auth export fragment with transactional command and receipt records. |
| [`DeterministicExportPackageAssembler.cs`](AuthManager.Infrastructure/Lifecycle/DeterministicExportPackageAssembler.cs) | Assembles a deterministic export ZIP and manifest from validated artifacts and supplies in-memory storage adapters. |

### Migrations

| File | Purpose |
| --- | --- |
| [`20260814120613_MicrosoftIdentity.cs`](AuthManager.Infrastructure/Migrations/20260814120613_MicrosoftIdentity.cs) | Creates the AuthManager Identity user, role, claim, login, and token tables and indexes. |
| [`20260814120613_MicrosoftIdentity.Designer.cs`](AuthManager.Infrastructure/Migrations/20260814120613_MicrosoftIdentity.Designer.cs) | Records EF Core’s target model for the initial ASP.NET Core Identity migration. |
| [`20260819155420_OnboardingOperationalFoundations.cs`](AuthManager.Infrastructure/Migrations/20260819155420_OnboardingOperationalFoundations.cs) | Creates the audit, idempotency, and outbox tables used by onboarding operations. |
| [`20260819155420_OnboardingOperationalFoundations.Designer.cs`](AuthManager.Infrastructure/Migrations/20260819155420_OnboardingOperationalFoundations.Designer.cs) | Records EF Core’s target model for the onboarding operational tables. |
| [`20260902192150_OrganisationMembershipDomain.cs`](AuthManager.Infrastructure/Migrations/20260902192150_OrganisationMembershipDomain.cs) | Creates the organisation, membership, and permission-set tables and seeds initial permission data. |
| [`20260902192150_OrganisationMembershipDomain.Designer.cs`](AuthManager.Infrastructure/Migrations/20260902192150_OrganisationMembershipDomain.Designer.cs) | Records EF Core’s target model for organisations, memberships, and permission sets. |
| [`20260910165430_TenantPermissionCatalogue.cs`](AuthManager.Infrastructure/Migrations/20260910165430_TenantPermissionCatalogue.cs) | Updates seeded permission sets and migrates existing permission catalogue data. |
| [`20260910165430_TenantPermissionCatalogue.Designer.cs`](AuthManager.Infrastructure/Migrations/20260910165430_TenantPermissionCatalogue.Designer.cs) | Records EF Core’s target model for the tenant permission catalogue revision. |
| [`20260912180002_PostgreSqlRuntimeRoles.cs`](AuthManager.Infrastructure/Migrations/20260912180002_PostgreSqlRuntimeRoles.cs) | Requires the provisioned Auth database roles and establishes their table, sequence, and migration-history privileges. |
| [`20260926154403_LifecycleAdmissionFoundation.cs`](AuthManager.Infrastructure/Migrations/20260926154403_LifecycleAdmissionFoundation.cs) | Creates tables for lifecycle operations, participants, and registry revisions, with database admission guards. |
| [`20260926154403_LifecycleAdmissionFoundation.Designer.cs`](AuthManager.Infrastructure/Migrations/20260926154403_LifecycleAdmissionFoundation.Designer.cs) | Records EF Core’s target model for lifecycle admission and participant registration. |
| [`20260928072552_MembershipPermissionGrants.cs`](AuthManager.Infrastructure/Migrations/20260928072552_MembershipPermissionGrants.cs) | Creates tenant-scoped membership permission grants with composite membership references and row-level security. |
| [`20260928072552_MembershipPermissionGrants.Designer.cs`](AuthManager.Infrastructure/Migrations/20260928072552_MembershipPermissionGrants.Designer.cs) | Records EF Core’s target model for membership permission grants. |
| [`20260928074856_LifecycleExportCoordinatorV1.cs`](AuthManager.Infrastructure/Migrations/20260928074856_LifecycleExportCoordinatorV1.cs) | Adds export fences, fragments, packages, coordinator leases, and inbox receipts with database guards. |
| [`20260928074856_LifecycleExportCoordinatorV1.Designer.cs`](AuthManager.Infrastructure/Migrations/20260928074856_LifecycleExportCoordinatorV1.Designer.cs) | Records EF Core’s target model for the lifecycle export coordinator. |
| [`20260928090251_LifecycleExportIntegrityV2.cs`](AuthManager.Infrastructure/Migrations/20260928090251_LifecycleExportIntegrityV2.cs) | Adds frozen export inventory metadata and Auth export execution, inbox, and outbox tables with integrity guards. |
| [`20260928090251_LifecycleExportIntegrityV2.Designer.cs`](AuthManager.Infrastructure/Migrations/20260928090251_LifecycleExportIntegrityV2.Designer.cs) | Records EF Core’s target model for the second lifecycle export integrity revision. |
| [`20260928150640_OrganisationClosureArchiveTransitionV1.cs`](AuthManager.Infrastructure/Migrations/20260928150640_OrganisationClosureArchiveTransitionV1.cs) | Adds closure and archive state, fence evidence, and guarded database transitions for organisations. |
| [`20260928150640_OrganisationClosureArchiveTransitionV1.Designer.cs`](AuthManager.Infrastructure/Migrations/20260928150640_OrganisationClosureArchiveTransitionV1.Designer.cs) | Records EF Core’s target model for organisation closure and archive transitions. |
| [`20260929175544_LifecycleRetentionEligibilityV1.cs`](AuthManager.Infrastructure/Migrations/20260929175544_LifecycleRetentionEligibilityV1.cs) | Adds retention decision sets and records and carries existing archive evidence into the disposition model. |
| [`20260929175544_LifecycleRetentionEligibilityV1.Designer.cs`](AuthManager.Infrastructure/Migrations/20260929175544_LifecycleRetentionEligibilityV1.Designer.cs) | Records EF Core’s target model for lifecycle retention eligibility. |
| [`20260929195328_Life04aPurgeProtocolV1.cs`](AuthManager.Infrastructure/Migrations/20260929195328_Life04aPurgeProtocolV1.cs) | Adds durable purge plans, progress, and outbox tables with row-level security. |
| [`20260929195328_Life04aPurgeProtocolV1.Designer.cs`](AuthManager.Infrastructure/Migrations/20260929195328_Life04aPurgeProtocolV1.Designer.cs) | Records EF Core’s target model for the LIFE-04A purge protocol. |
| [`20260929222145_Life05aVerificationFoundation.cs`](AuthManager.Infrastructure/Migrations/20260929222145_Life05aVerificationFoundation.cs) | Adds purge verification commands, evidence, and lifecycle bindings with row-level security. |
| [`20260929222145_Life05aVerificationFoundation.Designer.cs`](AuthManager.Infrastructure/Migrations/20260929222145_Life05aVerificationFoundation.Designer.cs) | Records EF Core’s target model for the LIFE-05A verification foundation. |
| [`20261014142758_Issue78OrganisationTimeZoneCore.cs`](AuthManager.Infrastructure/Migrations/20261014142758_Issue78OrganisationTimeZoneCore.cs) | Adds organisation time-zone settings and versions with a protected configuration audit table. |
| [`20261014142758_Issue78OrganisationTimeZoneCore.Designer.cs`](AuthManager.Infrastructure/Migrations/20261014142758_Issue78OrganisationTimeZoneCore.Designer.cs) | Records EF Core’s target model for organisation time zones and their audit records. |
| [`20261018144506_BrowserSessionSnapshot.cs`](AuthManager.Infrastructure/Migrations/20261018144506_BrowserSessionSnapshot.cs) | Creates the browser session family and refresh credential tables and their runtime grants. |
| [`20261018144506_BrowserSessionSnapshot.Designer.cs`](AuthManager.Infrastructure/Migrations/20261018144506_BrowserSessionSnapshot.Designer.cs) | Records EF Core’s target model for browser sessions and refresh credentials. |
| [`20261115195218_OnboardingRegistration.cs`](AuthManager.Infrastructure/Migrations/20261115195218_OnboardingRegistration.cs) | Creates tables for verification challenges, onboarding grants, operation receipts, and the verification mail outbox. |
| [`20261115195218_OnboardingRegistration.Designer.cs`](AuthManager.Infrastructure/Migrations/20261115195218_OnboardingRegistration.Designer.cs) | Records EF Core’s target model for registration and email verification. |
| [`20261124170000_OrganisationSingleActiveOwner.cs`](AuthManager.Infrastructure/Migrations/20261124170000_OrganisationSingleActiveOwner.cs) | Checks for existing conflicts and adds a partial unique index for one active owner per organisation. |
| [`20261124170000_OrganisationSingleActiveOwner.Designer.cs`](AuthManager.Infrastructure/Migrations/20261124170000_OrganisationSingleActiveOwner.Designer.cs) | Records EF Core’s target model for the single-active-owner index. |
| [`AuthDbContextModelSnapshot.cs`](AuthManager.Infrastructure/Migrations/AuthDbContextModelSnapshot.cs) | Records the current AuthManager EF Core model as the baseline for future migrations. |
| [`README.md`](AuthManager.Infrastructure/Migrations/README.md) | Documents the original Identity schema baseline, its deployment and rollback, and the additive onboarding operational tables. |

### Onboarding

| File | Purpose |
| --- | --- |
| [`LocalStackMailpitDelivery.cs`](AuthManager.Infrastructure/Onboarding/LocalStackMailpitDelivery.cs) | Sends registration verification messages to the private LocalStack Mailpit SMTP sink. |
| [`RegistrationOptions.cs`](AuthManager.Infrastructure/Onboarding/RegistrationOptions.cs) | Validates LocalStack registration and Mailpit settings, verification URLs and protected verification-key references. |
| [`RegistrationService.cs`](AuthManager.Infrastructure/Onboarding/RegistrationService.cs) | Atomically handles registration, verification challenges and mail staging, resends, token exchange and creation grants. |
| [`VerificationMailDispatcher.cs`](AuthManager.Infrastructure/Onboarding/VerificationMailDispatcher.cs) | Leases a verification mail record, rechecks challenge eligibility, sends it within the SMTP deadline and records the outcome. |
| [`VerificationMailWorker.cs`](AuthManager.Infrastructure/Onboarding/VerificationMailWorker.cs) | Polls the verification mail dispatcher in fresh scopes and logs failures without exposing provider exception details. |
| [`VerificationTokenService.cs`](AuthManager.Infrastructure/Onboarding/VerificationTokenService.cs) | Creates and matches keyed verification tokens bound to a challenge and hashes token, identity and grant bindings. |

### Outbox

| File | Purpose |
| --- | --- |
| [`LifecycleOutboxPublicationActivityClassifier.cs`](AuthManager.Infrastructure/Outbox/LifecycleOutboxPublicationActivityClassifier.cs) | Checks lifecycle outbox messages against their envelope and persisted operation before treating them as trusted control traffic. |
| [`OutboxBackgroundService.cs`](AuthManager.Infrastructure/Outbox/OutboxBackgroundService.cs) | Polls the Auth outbox dispatcher in scoped batches until the host stops. |
| [`OutboxDispatcher.cs`](AuthManager.Infrastructure/Outbox/OutboxDispatcher.cs) | Leases and publishes outbox messages, honoring organisation lifecycle fences and recording retries or dead letters. |
| [`OutboxDispatcherOptions.cs`](AuthManager.Infrastructure/Outbox/OutboxDispatcherOptions.cs) | Configures outbox enablement, batching, retry bounds, polling and lease duration. |
| [`UnconfiguredOutboxMessagePublisher.cs`](AuthManager.Infrastructure/Outbox/UnconfiguredOutboxMessagePublisher.cs) | Fails an outbox publish attempt when no actual message publisher is configured. |

### Persistence

| File | Purpose |
| --- | --- |
| [`AuthContextDatabaseMigration.cs`](AuthManager.Infrastructure/Persistence/AuthContextDatabaseMigration.cs) | Exposes a web application extension that resolves the scoped Auth DbContext and invokes EF migrations. |
| [`AuthDbContext.cs`](AuthManager.Infrastructure/Persistence/AuthDbContext.cs) | Defines the Auth Identity DbContext, persistent models, and lifecycle-scoped database behavior. |
| [`AuthOwnerSqlReceipt.cs`](AuthManager.Infrastructure/Persistence/AuthOwnerSqlReceipt.cs) | Observes selected EF command outcomes through an opt-in owner SQL evidence interceptor. |
| [`Configurations/AuditEntryConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/AuditEntryConfiguration.cs) | Maps audit entries, including correlation metadata and lookup indexes. |
| [`Configurations/BrowserSessionConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/BrowserSessionConfiguration.cs) | Maps browser session families and refresh credentials, with their ownership and lifespan-related fields. |
| [`Configurations/IdempotencyRecordConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/IdempotencyRecordConfiguration.cs) | Maps idempotency records and enforces unique request keys within their operation scope. |
| [`Configurations/MembershipPermissionGrantConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/MembershipPermissionGrantConfiguration.cs) | Maps tenant-scoped permission grants with membership relationships, active-grant uniqueness, and concurrency checks. |
| [`Configurations/OnboardingRegistrationConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/OnboardingRegistrationConfiguration.cs) | Maps the four registration and verification tables, including their relationships, indexes, and concurrency checks. |
| [`Configurations/OrganisationConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/OrganisationConfiguration.cs) | Maps organisation ownership, status, time-zone settings, and concurrency metadata. |
| [`Configurations/OrganisationMembershipConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/OrganisationMembershipConfiguration.cs) | Maps organisation memberships, their related entities, and the single-active-owner index. |
| [`Configurations/OutboxMessageConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs) | Maps general outbox messages, including payload and dispatch lookup indexes. |
| [`Configurations/PermissionSetConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/PermissionSetConfiguration.cs) | Maps permission sets and enforces unique catalogue codes. |
| [`Configurations/TimeZoneConfigurationAuditConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/TimeZoneConfigurationAuditConfiguration.cs) | Maps attributed organisation time-zone audit and approval records. |
| [`Configurations/UserConfiguration.cs`](AuthManager.Infrastructure/Persistence/Configurations/UserConfiguration.cs) | Maps Identity user profile fields and enforces normalized email uniqueness. |
| [`Entities/AuditEntry.cs`](AuthManager.Infrastructure/Persistence/Entities/AuditEntry.cs) | Represents a timestamped audit outcome attributed to an actor and subject. |
| [`Entities/BrowserSessionFamily.cs`](AuthManager.Infrastructure/Persistence/Entities/BrowserSessionFamily.cs) | Defines tenant-neutral browser session families and their rotating refresh credentials. |
| [`Entities/EmailVerificationChallenge.cs`](AuthManager.Infrastructure/Persistence/Entities/EmailVerificationChallenge.cs) | Represents a generation-bound verification challenge and records its state transitions. |
| [`Entities/IdempotencyRecord.cs`](AuthManager.Infrastructure/Persistence/Entities/IdempotencyRecord.cs) | Tracks an idempotent operation’s request hash, processing window, and safe completed response. |
| [`Entities/OnboardingGrant.cs`](AuthManager.Infrastructure/Persistence/Entities/OnboardingGrant.cs) | Represents an expiring onboarding grant linked to a verified challenge and user. |
| [`Entities/OnboardingOperationReceipt.cs`](AuthManager.Infrastructure/Persistence/Entities/OnboardingOperationReceipt.cs) | Links a completed onboarding operation to its challenge generation and any resulting grant or mail outbox item. |
| [`Entities/OutboxMessage.cs`](AuthManager.Infrastructure/Persistence/Entities/OutboxMessage.cs) | Tracks durable event delivery attempts, leases, retry scheduling, and terminal outcomes. |
| [`Entities/TimeZoneConfigurationAudit.cs`](AuthManager.Infrastructure/Persistence/Entities/TimeZoneConfigurationAudit.cs) | Records an immutable time-zone change audit and the one-use approval that authorized it. |
| [`Entities/VerificationMailOutboxMessage.cs`](AuthManager.Infrastructure/Persistence/Entities/VerificationMailOutboxMessage.cs) | Tracks versioned verification mail delivery, leases, retries, and terminal status. |
| [`Lifecycle/LifecycleClosureStore.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleClosureStore.cs) | Persists closure coordination, fence results, release, and recovery in serializable transactions. |
| [`Lifecycle/LifecycleDispositionStore.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleDispositionStore.cs) | Validates and persists retention decisions and disposition readiness against their frozen request. |
| [`Lifecycle/LifecycleExportStore.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleExportStore.cs) | Persists the export workflow from participant fences and fragments through package assembly and release. |
| [`Lifecycle/LifecycleMapping.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleMapping.cs) | Maps lifecycle registry, operation, participant, and evidence records to the Auth schema. |
| [`Lifecycle/LifecyclePersistence.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecyclePersistence.cs) | Guards lifecycle database commands and stores reviewed registry activation, lookup, and operation admission. |
| [`Lifecycle/LifecyclePurgeStore.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecyclePurgeStore.cs) | Persists fixture-scoped purge admission, admitted messages, and participant execution receipts. |
| [`Lifecycle/LifecycleRegistryRecords.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleRegistryRecords.cs) | Defines persistent lifecycle registry revisions, participant bindings, and the active revision pointer. |
| [`Lifecycle/LifecycleVerificationStore.cs`](AuthManager.Infrastructure/Persistence/Lifecycle/LifecycleVerificationStore.cs) | Issues purge verification challenges, records observations and validation evidence, and controls finalization. |
| [`MigrationContextFactory.cs`](AuthManager.Infrastructure/Persistence/MigrationContextFactory.cs) | Creates a design-time Auth DbContext for migration scaffolding and SQL generation. |
| [`OrganisationOwnerIndexContract.cs`](AuthManager.Infrastructure/Persistence/OrganisationOwnerIndexContract.cs) | Checks that PostgreSQL contains the exact partial unique index required for the active-owner invariant. |
| [`Services/AuditWriter.cs`](AuthManager.Infrastructure/Persistence/Services/AuditWriter.cs) | Validates audit metadata and persists an attributed audit entry. |
| [`Services/IdempotencyStore.cs`](AuthManager.Infrastructure/Persistence/Services/IdempotencyStore.cs) | Handles idempotent request starts, conflict detection, completion, and replay of safe results. |
| [`Services/MembershipPermissionGrantStore.cs`](AuthManager.Infrastructure/Persistence/Services/MembershipPermissionGrantStore.cs) | Grants or revokes membership permissions in tenant-scoped transactions after live owner and target eligibility checks. |
| [`Services/OutboxWriter.cs`](AuthManager.Infrastructure/Persistence/Services/OutboxWriter.cs) | Validates outbox payloads and persists versioned integration messages for later delivery. |
| [`TenantContextInitializer.cs`](AuthManager.Infrastructure/Persistence/TenantContextInitializer.cs) | Sets and verifies the PostgreSQL organisation context for the current transaction. |

### Provisioning

| File | Purpose |
| --- | --- |
| [`BrowserOrganisationCreationService.cs`](AuthManager.Infrastructure/Provisioning/BrowserOrganisationCreationService.cs) | Admits local browser-authorized organisation creation, checks the active session and enforces the ownership limit with the local agreement deferral. |
| [`OrganisationProvisioningPersistence.cs`](AuthManager.Infrastructure/Provisioning/OrganisationProvisioningPersistence.cs) | Provides provisioning transactions, owner-catalogue and count checks, and persistence for the organisation and its initial owner membership. |

### Security

| File | Purpose |
| --- | --- |
| [`OnboardingRegistrationSecurityContract.md`](AuthManager.Infrastructure/Security/OnboardingRegistrationSecurityContract.md) | Documents the registration migration's four Auth tables, exact least-privilege rules and manifest-admission evidence. |
| [`rls-manifest.v10.json`](AuthManager.Infrastructure/Security/rls-manifest.v10.json) | Defines the Auth PostgreSQL runtime-role baseline for Identity and control tables, sequences and default privileges. |
| [`rls-manifest.v11.json`](AuthManager.Infrastructure/Security/rls-manifest.v11.json) | Adds lifecycle operation and participant RLS, classifies the global registry tables and admits the current-organisation function. |
| [`rls-manifest.v12.json`](AuthManager.Infrastructure/Security/rls-manifest.v12.json) | Adds tenant protection and runtime and column grants for membership permission grants. |
| [`rls-manifest.v13.json`](AuthManager.Infrastructure/Security/rls-manifest.v13.json) | Adds tenant-protected Auth export records, export fencing behavior and corresponding runtime grants. |
| [`rls-manifest.v14.json`](AuthManager.Infrastructure/Security/rls-manifest.v14.json) | Adds tenant-protected closure records, closure write fences and the dedicated recovery function and role. |
| [`rls-manifest.v15.json`](AuthManager.Infrastructure/Security/rls-manifest.v15.json) | Adds tenant protection and least-privilege grants for retention decision sets and records. |
| [`rls-manifest.v16.json`](AuthManager.Infrastructure/Security/rls-manifest.v16.json) | Adds tenant protection and grants for lifecycle purge plans, progress and outbox records. |
| [`rls-manifest.v17.json`](AuthManager.Infrastructure/Security/rls-manifest.v17.json) | Adds tenant-protected verification commands and evidence, with grants for the verification issue sequence. |
| [`rls-manifest.v18.json`](AuthManager.Infrastructure/Security/rls-manifest.v18.json) | Adds tenant protection for time-zone configuration audits and the required runtime column grants. |
| [`rls-manifest.v19.json`](AuthManager.Infrastructure/Security/rls-manifest.v19.json) | Classifies browser session families and refresh credentials and defines their exact runtime grants. |
| [`rls-manifest.v20.json`](AuthManager.Infrastructure/Security/rls-manifest.v20.json) | Classifies the four registration, verification and onboarding tables and adds their exact table and column grants. |
| [`rls-manifest.v21.json`](AuthManager.Infrastructure/Security/rls-manifest.v21.json) | Binds the existing Auth security catalogue to the single-active-owner migration without changing the privilege entries. |
| [`SensitiveDataGuard.cs`](AuthManager.Infrastructure/Security/SensitiveDataGuard.cs) | Rejects JSON and metadata property names associated with sensitive data. |

### TimeZones

| File | Purpose |
| --- | --- |
| [`ConfigureTimeZoneApproval.cs`](AuthManager.Infrastructure/TimeZones/ConfigureTimeZoneApproval.cs) | Builds the canonical time-zone approval digest and validates signed approvals against trusted keys and canonical IANA zones. |
| [`ConfigureTimeZoneCore.cs`](AuthManager.Infrastructure/TimeZones/ConfigureTimeZoneCore.cs) | Implements the disabled privileged time-zone write core with version checks, audit receipts and uncertain-commit reconciliation. |

### Workers

| File | Purpose |
| --- | --- |
| [`WorkerIdentitySource.cs`](AuthManager.Infrastructure/Workers/WorkerIdentitySource.cs) | Revalidates owner authority and creates or validates passwordless worker identities and their organisation memberships. |
| [`WorkerRotationRegistration.cs`](AuthManager.Infrastructure/Workers/WorkerRotationRegistration.cs) | Registers M1 worker rotation options, the identity source and the persistence and rotation services. |
| [`WorkerRotationStore.cs`](AuthManager.Infrastructure/Workers/WorkerRotationStore.cs) | Executes worker preparation and prior-family revocation in a tenant-scoped transaction with delivery checks and idempotency records. |

## Tests

`Tests` Covers application and domain behavior plus real integration boundaries such as HTTPS, database security and lifecycle processing.

### Application.IntegrationTests

| File | Purpose |
| --- | --- |
| [`Application.IntegrationTests.csproj`](Tests/Application.IntegrationTests/Application.IntegrationTests.csproj) | Defines the .NET 8 application integration test project and references AuthManager application services. |
| [`ApplicationDependencyInjectionTests.cs`](Tests/Application.IntegrationTests/ApplicationDependencyInjectionTests.cs) | Verifies application dependency registration resolves services and validates a registration request. |
| [`GlobalUsings.cs`](Tests/Application.IntegrationTests/GlobalUsings.cs) | Imports xUnit throughout the application integration test project. |
| [`packages.lock.json`](Tests/Application.IntegrationTests/packages.lock.json) | Locks the resolved NuGet dependencies for the application integration tests. |

### Application.UnitTests

| File | Purpose |
| --- | --- |
| [`Application.UnitTests.csproj`](Tests/Application.UnitTests/Application.UnitTests.csproj) | Defines the .NET 8 AuthManager application unit test project. |
| [`BrowserOrganisationCreationTests.cs`](Tests/Application.UnitTests/BrowserOrganisationCreationTests.cs) | Tests verified local organisation creation, identity locking, transaction rollback, replay, ownership caps, and agreement deferral. |
| [`GlobalUsings.cs`](Tests/Application.UnitTests/GlobalUsings.cs) | Imports xUnit throughout the application unit test project. |
| [`OrganisationProvisioningTests.cs`](Tests/Application.UnitTests/OrganisationProvisioningTests.cs) | Tests owner provisioning authority, idempotent replay, agreement refusal, identity binding, and an atomic transaction boundary. |
| [`packages.lock.json`](Tests/Application.UnitTests/packages.lock.json) | Locks the resolved NuGet dependencies for the application unit tests. |
| [`RegisterUserRequestValidatorTests.cs`](Tests/Application.UnitTests/RegisterUserRequestValidatorTests.cs) | Tests rejection of empty names in registration requests. |
| [`ServiceManagerTests.cs`](Tests/Application.UnitTests/ServiceManagerTests.cs) | Verifies the authentication facade and the application's dependency boundary with outer layers. |
| [`WorkerRotationTests.cs`](Tests/Application.UnitTests/WorkerRotationTests.cs) | Tests worker rotation admission, exact delivery metadata, and private result redaction. |

### Domain.UnitTests

| File | Purpose |
| --- | --- |
| [`Domain.UnitTests.csproj`](Tests/Domain.UnitTests/Domain.UnitTests.csproj) | Defines the .NET 8 domain unit test project and references AuthManager core. |
| [`GlobalUsings.cs`](Tests/Domain.UnitTests/GlobalUsings.cs) | Imports xUnit throughout the domain unit test project. |
| [`LifecycleClosureStateTests.cs`](Tests/Domain.UnitTests/LifecycleClosureStateTests.cs) | Tests the frozen closure participant inventory, archive admission, and pre-archive fence recovery. |
| [`LifecycleExportInventoryTests.cs`](Tests/Domain.UnitTests/LifecycleExportInventoryTests.cs) | Tests that lifecycle export inventory is canonical, registry-bound, and tamper-evident. |
| [`LifecycleRetentionStateTests.cs`](Tests/Domain.UnitTests/LifecycleRetentionStateTests.cs) | Tests retention and purge state transitions, frozen disposition ownership, and rejection of expired or held obligations. |
| [`MembershipPermissionGrantTests.cs`](Tests/Domain.UnitTests/MembershipPermissionGrantTests.cs) | Tests permission grant identity and revocation history, including repeated and backdated revocation. |
| [`OrganisationMembershipTests.cs`](Tests/Domain.UnitTests/OrganisationMembershipTests.cs) | Tests membership lifecycle, stable system permission sets, and owner-specific suspension and departure restrictions. |
| [`OrganisationTests.cs`](Tests/Domain.UnitTests/OrganisationTests.cs) | Tests organisation identity, lifecycle transitions, and refusal of invalid or stale changes. |
| [`packages.lock.json`](Tests/Domain.UnitTests/packages.lock.json) | Locks the resolved NuGet dependencies for the domain unit tests. |
| [`TenantPermissionTests.cs`](Tests/Domain.UnitTests/TenantPermissionTests.cs) | Tests the accepted permission matrix and fail-closed handling of unsupported roles or worker profiles. |
| [`UserStatusTests.cs`](Tests/Domain.UnitTests/UserStatusTests.cs) | Tests supported user lifecycle states and the domain project's dependency boundary. |

### Infrastructure.IntegrationTests

| File | Purpose |
| --- | --- |
| [`AssigneeEligibilityEndpointTests.cs`](Tests/Infrastructure.IntegrationTests/AssigneeEligibilityEndpointTests.cs) | Tests workload authentication, non-disclosing eligibility responses, JWKS rotation, and bounded authority failures. |
| [`AssigneeEligibilityResolverTests.cs`](Tests/Infrastructure.IntegrationTests/AssigneeEligibilityResolverTests.cs) | Tests fresh transactional eligibility decisions, bounded retries, revocation, and refusal of ambiguous membership. |
| [`AuthenticationStartupTests.cs`](Tests/Infrastructure.IntegrationTests/AuthenticationStartupTests.cs) | Runs shared real-host authentication startup checks against Auth's PostgreSQL bootstrap fixture. |
| [`AuthOwnerSqlReceiptComponentTests.cs`](Tests/Infrastructure.IntegrationTests/AuthOwnerSqlReceiptComponentTests.cs) | Tests source-bound SQL receipts from owner validation and preservation of a real lock failure. |
| [`AuthOwnerSqlReceiptTests.cs`](Tests/Infrastructure.IntegrationTests/AuthOwnerSqlReceiptTests.cs) | Tests EF SQL receipt capture, metadata admission, dispatch attribution, and safe reset after failures or cancellation. |
| [`AuthTestMigrationInitializer.cs`](Tests/Infrastructure.IntegrationTests/AuthTestMigrationInitializer.cs) | Applies Auth migrations to a fresh database for integration tests. |
| [`BearerValidationTests.cs`](Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs) | Runs the shared bearer validator contract against AuthManager authentication registration. |
| [`BrowserEligibleOrganisationsEndpointTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserEligibleOrganisationsEndpointTests.cs) | Tests eligible-organisation discovery bearer controls, complete responses, non-disclosure, and owned resource cleanup. |
| [`BrowserOrganisationCreationPolicyTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserOrganisationCreationPolicyTests.cs) | Tests local-only organisation creation enablement, default ownership caps, and refusal of invalid options. |
| [`BrowserOrganisationCreationPostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserOrganisationCreationPostgreSqlTests.cs) | Tests atomic browser organisation creation, serialized ownership claims, durable replay, and commit uncertainty on PostgreSQL. |
| [`BrowserOrganisationEndpointTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserOrganisationEndpointTests.cs) | Tests browser organisation creation and selection endpoints, including HTTPS, CSRF, exact request contracts, and non-disclosure. |
| [`BrowserRegistrationBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserRegistrationBoundaryTests.cs) | Tests registration body limits, input validation, rate budgets, cancellation, and restricted onboarding cookie emission. |
| [`BrowserSessionPostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserSessionPostgreSqlTests.cs) | Tests PostgreSQL browser session rotation, expiry, revocation, transaction uncertainty, and user lock ordering. |
| [`BrowserSessionTransportTests.cs`](Tests/Infrastructure.IntegrationTests/BrowserSessionTransportTests.cs) | Tests browser session transport, exact origin and CSRF rules, proxy attribution, cookie behavior, and bounded throttling. |
| [`Configuration/Test-Configuration.ps1`](Tests/Infrastructure.IntegrationTests/Configuration/Test-Configuration.ps1) | Checks LocalStack Compose configuration, private configuration inputs, health probes, and deployment safeguards. |
| [`CurrentTenantAccessTests.cs`](Tests/Infrastructure.IntegrationTests/CurrentTenantAccessTests.cs) | Tests fresh tenant authority decisions and refusal after unsupported grants or worker membership changes. |
| [`EligibleOrganisationsPostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/EligibleOrganisationsPostgreSqlTests.cs) | Tests PostgreSQL organisation discovery and selection, including bounded ordering, session state, cancellation, and fresh authority. |
| [`GatewayBrowserTransportTests.cs`](Tests/Infrastructure.IntegrationTests/GatewayBrowserTransportTests.cs) | Tests the real Ocelot browser route allowlist, forwarded HTTPS contracts, request attribution, and mutation recovery. |
| [`GatewayLocalStackHealthTests.cs`](Tests/Infrastructure.IntegrationTests/GatewayLocalStackHealthTests.cs) | Tests Gateway local health registration, browser route admission, HTTPS forwarding, and sanitized readiness failures. |
| [`GlobalUsings.cs`](Tests/Infrastructure.IntegrationTests/GlobalUsings.cs) | Imports xUnit throughout the infrastructure integration test project. |
| [`GmailEmailVerificationDeliveryTests.cs`](Tests/Infrastructure.IntegrationTests/GmailEmailVerificationDeliveryTests.cs) | Tests Gmail verification delivery input, credential access, cancellation, deadline, and safe SMTP failure handling. |
| [`IdentityFoundationTests.cs`](Tests/Infrastructure.IntegrationTests/IdentityFoundationTests.cs) | Tests pending identity creation, password policy, unique normalized email, lockout, and token support. |
| [`IdentityTestContext.cs`](Tests/Infrastructure.IntegrationTests/IdentityTestContext.cs) | Builds and disposes a SQLite-backed identity context for integration tests. |
| [`Infrastructure.IntegrationTests.csproj`](Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj) | Defines the integration suite for AuthManager, Ocelot Gateway, and shared security contracts. |
| [`IssuerContractTests.cs`](Tests/Infrastructure.IntegrationTests/IssuerContractTests.cs) | Tests issuer acceptance across APIs, signing key rotation and readiness, public JWKS, and browser token claims. |
| [`JwksHttpSourceTests.cs`](Tests/Infrastructure.IntegrationTests/JwksHttpSourceTests.cs) | Tests JWKS HTTP response limits with or without a content length and refusal of unsuccessful responses. |
| [`Life05aFailClosedContractTests.cs`](Tests/Infrastructure.IntegrationTests/Life05aFailClosedContractTests.cs) | Tests rejection of forged, stale, mismatched, or false lifecycle purge evidence. |
| [`Life05aProductionIsolationTests.cs`](Tests/Infrastructure.IntegrationTests/Life05aProductionIsolationTests.cs) | Verifies production composition omits the LIFE05a fixture verifier, finalizer, route, and worker. |
| [`LifecycleClosureCoordinatorEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/LifecycleClosureCoordinatorEvidenceTests.cs) | Tests durable PostgreSQL closure coordination, participant fencing, replay, and fixture-only retention and purge boundaries. |
| [`LifecycleExportCoordinatorEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/LifecycleExportCoordinatorEvidenceTests.cs) | Tests restart-safe PostgreSQL export coordination, deterministic package assembly, and snapshot fencing. |
| [`LifecycleFrozenRegistryEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/LifecycleFrozenRegistryEvidenceTests.cs) | Tests immutable lifecycle registry bindings, PostgreSQL ACL and RLS barriers, and current admission authority. |
| [`LinkedClientActionWriteConflictException.cs`](Tests/Infrastructure.IntegrationTests/LinkedClientActionWriteConflictException.cs) | Provides the client action conflict exception type for linked integration test sources. |
| [`LocalStackHealthTests.cs`](Tests/Infrastructure.IntegrationTests/LocalStackHealthTests.cs) | Tests local health behavior, current issuer readiness, sanitized failure reporting, and business transport boundaries. |
| [`ManualTimeProvider.cs`](Tests/Infrastructure.IntegrationTests/ManualTimeProvider.cs) | Provides a manually controlled UTC clock for integration tests. |
| [`OnboardingPersistenceFoundationTests.cs`](Tests/Infrastructure.IntegrationTests/OnboardingPersistenceFoundationTests.cs) | Tests onboarding idempotency, atomic audit and outbox writes, dispatcher retry and dead-letter handling, and credential-field refusal. |
| [`OrganisationPersistenceTests.cs`](Tests/Infrastructure.IntegrationTests/OrganisationPersistenceTests.cs) | Tests organisation persistence constraints, system permission seeds, membership concurrency, and tenant-neutral user identity. |
| [`OrganisationProvisioningPersistenceTests.cs`](Tests/Infrastructure.IntegrationTests/OrganisationProvisioningPersistenceTests.cs) | Tests atomic owner provisioning, durable replay, rollback and commit uncertainty, and refusal of forged authority. |
| [`OrganisationTimeZoneEndpointTests.cs`](Tests/Infrastructure.IntegrationTests/OrganisationTimeZoneEndpointTests.cs) | Tests service-only time zone reads and the closed boundary for disabled write ingress. |
| [`packages.lock.json`](Tests/Infrastructure.IntegrationTests/packages.lock.json) | Locks the resolved NuGet dependencies for the infrastructure integration tests. |
| [`PermissionMigrationTests.cs`](Tests/Infrastructure.IntegrationTests/PermissionMigrationTests.cs) | Tests that legacy membership migration preserves reviewed mappings without inferring a role. |
| [`PgFixturePurgeParticipant.cs`](Tests/Infrastructure.IntegrationTests/PgFixturePurgeParticipant.cs) | Implements fixture-only PostgreSQL purge participation under the synthetic write fence. |
| [`PgFixturePurgeVerifier.cs`](Tests/Infrastructure.IntegrationTests/PgFixturePurgeVerifier.cs) | Observes PostgreSQL fixture state for independent lifecycle purge verification. |
| [`PostgreSqlFixture.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlFixture.cs) | Provides a shared PostgreSQL container fixture with isolated database creation and asynchronous cleanup. |
| [`PostgreSqlMembershipPermissionGrantTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlMembershipPermissionGrantTests.cs) | Tests PostgreSQL owner-controlled permission grants, concurrent deduplication, revocation history, and cross-organisation RLS. |
| [`PostgreSqlOrganisationPersistenceTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlOrganisationPersistenceTests.cs) | Tests PostgreSQL organisation migration constraints and concurrent duplicate membership refusal. |
| [`PostgreSqlOrganisationProvisioningTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlOrganisationProvisioningTests.cs) | Tests restricted PostgreSQL organisation provisioning, atomic rollback, tenant context, and concurrent replay. |
| [`PostgreSqlRoleCatalogEvidenceTests.cs`](Tests/Infrastructure.IntegrationTests/PostgreSqlRoleCatalogEvidenceTests.cs) | Tests PostgreSQL role and catalog conformance, bootstrap migration, external fixture admission, and redacted diagnostics. |
| [`RegistrationOptionsBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/RegistrationOptionsBoundaryTests.cs) | Tests registration feature admission, local mail restrictions, exact HTTPS configuration, and verification key file safeguards. |
| [`RegistrationPostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/RegistrationPostgreSqlTests.cs) | Tests PostgreSQL registration and exchange concurrency, atomic rollback, replay, expiry, resend limits, and cancellation. |
| [`RegistrationServiceBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/RegistrationServiceBoundaryTests.cs) | Tests registration validation before storage, verification identity binding, and live restricted grant selection. |
| [`RegistrationTransportTests.cs`](Tests/Infrastructure.IntegrationTests/RegistrationTransportTests.cs) | Tests registration over the real transport boundary, including readiness, scoped cookies, exact trust, and cancellation. |
| [`RegistrationVerificationKeyFixture.cs`](Tests/Infrastructure.IntegrationTests/RegistrationVerificationKeyFixture.cs) | Creates or borrows private verification key fixture files and tests scope, refusal, and owned cleanup. |
| [`RuntimeConnectionConfigurationTests.cs`](Tests/Infrastructure.IntegrationTests/RuntimeConnectionConfigurationTests.cs) | Tests that AuthManager requires a nonblank runtime connection configuration. |
| [`SyntheticFixtureWriteFence.cs`](Tests/Infrastructure.IntegrationTests/SyntheticFixtureWriteFence.cs) | Provides a scoped lifecycle write fence for synthetic PostgreSQL fixtures. |
| [`TenantAccessEndpointTests.cs`](Tests/Infrastructure.IntegrationTests/TenantAccessEndpointTests.cs) | Tests that tenant access endpoints use bearer authentication even with an identity cookie default. |
| [`TimeZoneCorePostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/TimeZoneCorePostgreSqlTests.cs) | Tests PostgreSQL time zone privilege boundaries and atomic redemption of synthetic signed approval. |
| [`VerificationMailDispatcherBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/VerificationMailDispatcherBoundaryTests.cs) | Tests verification mail dispatch eligibility, lease boundaries, bounded retries, and cancellation semantics. |
| [`VerificationMailpitTests.cs`](Tests/Infrastructure.IntegrationTests/VerificationMailpitTests.cs) | Tests private Mailpit SMTP delivery, retry token stability, and challenge invalidation during resend. |
| [`VerificationMailWorkerBoundaryTests.cs`](Tests/Infrastructure.IntegrationTests/VerificationMailWorkerBoundaryTests.cs) | Tests bounded verification mail worker batches, scope cleanup, cancellation, and sanitized provider failure reporting. |
| [`VerificationTokenTests.cs`](Tests/Infrastructure.IntegrationTests/VerificationTokenTests.cs) | Tests verification token canonicalization, key rotation, tamper and rebinding refusal, and fail-closed configuration. |
| [`WorkerRotationEndpointContractTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerRotationEndpointContractTests.cs) | Tests worker rotation HTTPS and bearer admission, current session checks, safe outcomes, and one store invocation. |
| [`WorkerRotationPersistenceTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerRotationPersistenceTests.cs) | Tests durable worker issuance, exact delivery-bound revocation, transactional rollback, and least-privilege profile validation. |
| [`WorkerRotationPostgreSqlTests.cs`](Tests/Infrastructure.IntegrationTests/WorkerRotationPostgreSqlTests.cs) | Tests PostgreSQL worker rotation refusal when owner authority changes after precheck. |
