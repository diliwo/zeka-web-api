# Issue #44 — review corrections

Reviewed baseline: `8f75258400da679a189d619d310e6cbfefe903c1`.
Branch: `feature/issue-44-tenant-enforcement`. Validation date: 2026-09-11.

## Scope and architecture

Corrected the five implementation findings under Issue #44. Consulted the current,
fast-forwarded Ubongo main checkout: accepted ADR-001, ADR-002, ADR-003, accepted
Tenant Authorization and Permissions, and the independent review of the baseline.

The permission catalogue, authoritative AuthManagement live-state resolver, migration
mapping safeguards, tenancy-library enforcement and PostgreSQL fixture structure are
preserved. No authentication validator, migration, public event contract or external
repository implementation changed. No push, PR, deployment or live migration was performed.

## Corrections and focused evidence

All cases below passed in the final Release suite.

| Finding | Implementation | Focused evidence |
| --- | --- | --- |
| Username-based mysupports | Handler supplies only the authorized TenantOperation.MembershipId. Repository uses membership identity and the canonical assignment query for both assigned and all-scope callers. | MySupportsAuthorizationTests: 3; CurrentAssignmentTests: 16. Covers both permission scopes, misleading usernames, missing linkage, another organisation with the same membership GUID, and PostgreSQL rejection of cross-organisation worker links. |
| Autonomous outbox recovery | Registered hosted worker; explicit organisation enrollment; fresh live authorization and tenant scope on every cycle; durable pending rows retried after restart. Missing enrollment prevents staff staging; missing worker configuration fails startup. | StaffRetryWorkerTests: 5; StaffOutboxTests: 2. Real hosted-loop transient failure/recovery publishes a tombstone without a second user mutation; pending rows in another organisation stay untouched; authority denial/restoration and later revocation are checked; 101-row batch/poison-record test proves bounded progress. |
| Current-access outcomes, retries and telemetry | Distinct Unauthenticated/Denied/Unavailable propagate through Application and middleware. Target-membership adapters also preserve 401. Current-access GET retries at most once for transient failures within a shared 2-second deadline. | AccessResilienceTests: 21 per downstream service, plus TenantHttpTests in both. Covers 401/403/503, 429/500, malformed JSON, unknown/duplicate permissions, version and linkage errors, missing credentials, retry success/exhaustion, no success cache, overall deadline and caller cancellation, safe metric/trace dimensions. |
| Canonical current assignment | CurrentAssignment.ActiveOn(date): not deleted, start <= date, end absent or > date. AssignedClients requires exactly one such support with active, same-organisation membership linkage. Used by reads, assigned-write guards, mysupports, domain IsActif, and support persistence/date validation. | CurrentAssignmentTests: 16; AssignedClientTests: 7. Future start, future end, ended, deleted, zero/multiple active supports; detached writes; support persistence checks. StaffProjectionTests proves a consumed tombstone removes assigned visibility and stale revisions cannot restore it. |
| Duplicate EF references | Removed duplicate Tools, Relational and Design references from the three affected project files. | XML inspection of all 15 project files touched by the reviewed branch found zero duplicate PackageReference groups. Final build has no NU1504 warnings. |

### Outbox bounds and recovery

- At most 100 configured organisations, selected explicitly and authorized independently.
- At most 100 records per tenant batch; ten-second tenant budget; two-second broker wait.
- Five-second normal cycle interval, capped thirty-second backoff for cycle/access failures.
- Failed rows retain payload and pending status. Existing audit timestamps record attempts
  and rotate failed rows behind older pending work; poison rows do not stop later records.
- Publication remains at least once; consumer revision/event identity guards handle replay.
- Metrics: `staff_outbox.publications`, `staff_outbox.pending_age`,
  `staff_outbox.retry_cycles`, under `AdminAreaManagement.StaffOutbox`.
- Warnings and metrics contain no payload, credential, membership or organisation identifier.
- The broker port has no cancellation argument; the dispatcher bounds its wait, and
  timeout/recovery may produce duplicate delivery. The versioned consumer remains idempotent.

### Access budget and observability

Current-access calls allow two GET attempts total, separated by 100 ms only after a
transient HTTP 408/429/5xx or transport failure. The two-second overall deadline covers
both attempts, delay and response body. 401 is Unauthenticated, 403 is Denied;
dependency timeout and invalid contracts are Unavailable. Caller cancellation propagates.
Separate target-membership verification retains its existing two-second budget.

`AdminAreaManagement.TenantAccess` and `ClientManagement.TenantAccess` publish
`tenant_access.attempts`, `tenant_access.authority_failures`, and
`tenant_access.duration`, plus a `tenant_access.resolve` activity.
Dimensions are bounded attempt number, outcome, contract version and failure category.
Tests subscribe to the real Meter/ActivitySource and verify redaction. No exporter,
telemetry vendor or authorization cache was introduced.

## Full Release evidence

SDK: 8.0.129; PostgreSQL 17 Testcontainers, using the local Docker engine.

```text
dotnet build zeka-web-api.sln -c Release --no-restore --verbosity quiet
Exit 0: build succeeded, 0 errors, 2 NU1903 warnings.

dotnet test zeka-web-api.sln -c Release --no-build --no-restore
  --logger "trx;LogFileName=issue44-release.trx" --verbosity quiet
Exit 0: 393 passed, 0 failed, 0 skipped across 12 test projects.
```

| Service | Domain unit | Application unit | Application integration | Infrastructure integration | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| AdminAreaManagement | 60 | 42 | 4 | 87 | 193 |
| AuthManagement | 23 | 3 | 1 | 34 | 61 |
| ClientManagement | 32 | 16 | 15 | 76 | 139 |
| Total | 115 | 61 | 20 | 197 | 393 |

Local raw test evidence is in each test project's
`TestResults/issue44-release.trx` (ignored generated files). Build and aggregate
test logs are in the local temporary directory as `issue44-release-build.log` and
`issue44-release-test.log`.

The initial compilation emitted 728 warnings; the final incremental solution build
emitted the two existing AutoMapper 13.0.1 NU1903 advisory warnings
(GHSA-rvv3-g6hj-g44x). This is not a warning-free build. Unrelated dependency and
compiler-warning cleanup was deliberately left outside these corrections.

Fixture failures found during development were diagnosed and repaired without
weakening production guards: immutable membership cannot be corrupted through EF;
EF Reload uses a query path rejected by the tenancy guard, so verification now
uses a fresh tenant-scoped query. All final tests above passed.

## Acceptance and review

The original Issue #44 acceptance mapping remains in
[issue-44-tenant-enforcement.md](issue-44-tenant-enforcement.md). These corrections
strengthen membership-based resource authorization, fail-closed outcomes, explicit
background scope establishment and recovery, while retaining the original provider
and isolation conformance tests. No test was skipped or weakened.

Explicit self-review covered the membership comparison, SQL temporal/count predicates,
detached-write guard reuse, autonomous scope isolation, authoritative worker access,
publication/replay behavior, timeout/cancellation limits, and redacted observability.
Independent delivery review remains appropriate under the authentication gate.

Deterministic secrets scans passed for corrected code and reviewed test evidence.
`git diff --check` passed before the requested local commit; final commit and branch
cleanliness are reported in the accompanying response.

## Only remaining architecture decision: authentication

Delivery remains blocked on the authoritative issuer and validator-only JWT contract.
The existing shared HS256 trust model was not changed and is not accepted for delivery.

1. Preferred: an approved authoritative asymmetric issuer, with downstream public-key
   validation via an approved HTTPS JWKS endpoint and no downstream signing capability.
2. Alternative: that asymmetric issuer with explicitly approved pinned public-key
   provisioning and coordinated rotation.

Approval must identify the issuer owner, issuer/audience/subject contract, allowed
algorithm, rotation and unknown-key/outage behavior, and downstream/worker credential
compatibility. No issuer, key-distribution mechanism, Azure design or broader
authentication platform was selected or implemented here. Existing migration,
credential provisioning and RLS follow-up prerequisites remain as documented;
these tests are not a production-readiness claim.

## Changed files

- [Docs/issue-44-review-corrections.md](../Docs/issue-44-review-corrections.md)
- [Docs/issue-44-tenant-enforcement.md](../Docs/issue-44-tenant-enforcement.md)
- [Services/AdminAreaManagement/AdminAreaManagement.Application/Common/Authorization/TenantAccess.cs](../Services/AdminAreaManagement/AdminAreaManagement.Application/Common/Authorization/TenantAccess.cs)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/AdminAreaManagement.Infrastructure.csproj](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/AdminAreaManagement.Infrastructure.csproj)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Authorization/CurrentTenantAccessClient.cs](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Authorization/CurrentTenantAccessClient.cs)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Authorization/StaffMembershipClient.cs](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Authorization/StaffMembershipClient.cs)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionOutbox.cs](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionOutbox.cs)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionRetryWorker.cs](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionRetryWorker.cs)
- [Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/TenantRegistration.cs](../Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/TenantRegistration.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/StaffOutboxTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/StaffOutboxTests.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/StaffRetryWorkerTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/StaffRetryWorkerTests.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs)
- [Services/ClientManagement/Client.API/ClientManagement.API.csproj](../Services/ClientManagement/Client.API/ClientManagement.API.csproj)
- [Services/ClientManagement/Client.Application/Common/Authorization/TenantAccess.cs](../Services/ClientManagement/Client.Application/Common/Authorization/TenantAccess.cs)
- [Services/ClientManagement/Client.Application/Supports/Queries/GetSupportsByReferents/GetSupportsBySocialWorkersQuery.cs](../Services/ClientManagement/Client.Application/Supports/Queries/GetSupportsByReferents/GetSupportsBySocialWorkersQuery.cs)
- [Services/ClientManagement/Client.Core/Entities/CurrentAssignment.cs](../Services/ClientManagement/Client.Core/Entities/CurrentAssignment.cs)
- [Services/ClientManagement/Client.Core/Entities/SocialCase.cs](../Services/ClientManagement/Client.Core/Entities/SocialCase.cs)
- [Services/ClientManagement/Client.Core/Interfaces/ISupportRepository.cs](../Services/ClientManagement/Client.Core/Interfaces/ISupportRepository.cs)
- [Services/ClientManagement/Client.Infrastructure/Authorization/CurrentTenantAccessClient.cs](../Services/ClientManagement/Client.Infrastructure/Authorization/CurrentTenantAccessClient.cs)
- [Services/ClientManagement/Client.Infrastructure/Authorization/StaffMembershipClient.cs](../Services/ClientManagement/Client.Infrastructure/Authorization/StaffMembershipClient.cs)
- [Services/ClientManagement/Client.Infrastructure/ClientManagement.Infrastructure.csproj](../Services/ClientManagement/Client.Infrastructure/ClientManagement.Infrastructure.csproj)
- [Services/ClientManagement/Client.Infrastructure/Persistence/ApplicationDbContext.cs](../Services/ClientManagement/Client.Infrastructure/Persistence/ApplicationDbContext.cs)
- [Services/ClientManagement/Client.Infrastructure/Persistence/SupportRepository.cs](../Services/ClientManagement/Client.Infrastructure/Persistence/SupportRepository.cs)
- [Services/ClientManagement/Tests/Application.UnitTests/MySupportsAuthorizationTests.cs](../Services/ClientManagement/Tests/Application.UnitTests/MySupportsAuthorizationTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/AccessResilienceTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/AssignedClientTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/AssignedClientTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/CurrentAssignmentTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/CurrentAssignmentTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/StaffProjectionTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/StaffProjectionTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs)
