# Issue #44: explicit tenant operations and persistence enforcement

## Authority and scope

Implementation baseline: accepted Ubongo ADR-001, ADR-002 and ADR-003; accepted
Tenant Authorization and Permissions; accepted onboarding architecture; issue #43
PostgreSQL migration evidence; tenancy-library contracts and tests. Proposed
Organisation Design material is not an architectural decision.

All three tenancy packages are pinned to `2.0.0-preview.1`. Domain references only
Abstractions, Infrastructure consumes EntityFrameworkCore, and API consumes
AspNetCore. Application and Domain do not reference ASP.NET Core, gRPC or Npgsql.
Existing gRPC generation and JSON Patch adaptation now belong to delivery/adapters.

## Operation boundary

HTTP validates RS256 JWTs using public JWKS material, exact environment issuer,
one common Zeka API audience, lifetime and mandatory kid. AuthManager owns the
replaceable v1 issuer; downstream APIs have no private signing material. See
[the accepted authentication implementation and evidence](issue-44-authentication.md).
The authenticated
subject and single `X-Organisation-Id` selection feed Application's current-access
port; selection and role claims do not grant access. Conflicting organisation or
subject claims fail closed. Application request attributes are authoritative;
HTTP request metadata derives its permission from the same request type.

AuthManagement's `GET /api/v1/tenant-access/{organisationId}` evaluates current
organisation, membership, user and fixed permission-set state in one database
query. The v1 response contains subject, organisation, membership, effective
permissions, decision version and observation time. A successful response is
never cached. Unknown contracts, roles, permission codes or mismatched identities
fail closed. Seven fixed roles implement the accepted catalogue exactly.

Each current-access adapter call has at most two safe GET attempts and a shared
two-second deadline, including the 100 ms retry delay and body consumption.
Only transport failures, HTTP 408/429 and 5xx are retried. Authentication failures,
semantic denials and invalid contracts are not retried. There is no success cache.
Unauthenticated requests return 401; denials return 403; dependency failures and
timeouts return 503. Caller cancellation propagates. Responses use `no-store`.
Staff creation and new consumer revisions perform a separate bounded membership
verification, so their authorization dependency budget can reach four seconds in total. Production
latency/availability qualification remains part of the production-readiness gate.
The `AdminAreaManagement.TenantAccess` and `ClientManagement.TenantAccess`
ActivitySource/Meter names expose decision duration, attempt count, contract
version, bounded outcome and authority-failure reason. They emit no subject,
organisation, membership, permission list, credentials, response body or exception
message. Operators can subscribe through the existing diagnostics tooling; this
slice introduces no telemetry exporter/vendor. Outbox failure logging contains no
event payload.

Only successful Application authorization establishes the immutable scoped
`TenantContextScope`. It works without HTTP and cannot be reused for another
organisation, subject, membership or resource policy. Job hosts must create and
dispose a DI scope per operation and supply their own authenticated identity and
credential adapters. The registered `StaffProjectionRetryWorker` authorizes each
fresh scope before invoking the `DispatchStaffProjectionsCommand` handler.

## Persistence and resource authorization

Both operational service contexts inherit the released `TenantDbContext`.
Unclassified, ambiguously classified and owned EF entity mappings fail model
construction. Tenant filters compose with existing soft-delete filters. New rows
are stamped from context; conflicting or changed tenant IDs are rejected in every
SaveChanges overload. Organisation IDs participate in optimistic concurrency,
including detached updates/deletes. Composite issue #43 foreign keys remain intact.

Runtime raw SQL, scalar SQL, bulk writes and `IgnoreQueryFilters` are rejected.
Deployment contexts are separate, have no application DI registration and are used
only by design-time tooling and deployment/migration fixtures. These are trusted
code boundaries; PostgreSQL roles/RLS remain issue #45, not an assertion of this slice.

Assigned-only client queries include a SQL predicate requiring exactly one current,
non-deleted support whose non-deleted worker has the current immutable membership
ID in the same organisation. Missing and ambiguous assignments yield no rows.
`CurrentAssignment.ActiveOn(date)` is the canonical temporal predicate:
not deleted, StartDate <= today, and EndDate absent or > today (inclusive start,
exclusive end). Future-start supports do not grant early access; future-end
supports remain active. Reads, detached-write guards, domain active checks and
`mysupports` share this rule. The latter derives its membership exclusively from
`TenantOperation.MembershipId`, including callers with all-scope permission.
Its optional history flag never broadens access beyond that membership's currently
assigned clients. Usernames remain display data only.
Related-resource reads and writes apply the same predicate, including detached
writes. Full-scope permission still retains organisation isolation. Legacy
assessments without a proven client link remain unassigned and inaccessible to
assigned-only operations. No link is inferred from a name or unrelated numeric ID.

Optional value objects use scalar conversions/flattened properties. Partner contact
and email rows are explicit tenant-owned entities preserving their existing tables,
columns and composite keys. Existing timestamp column types are preserved; issue
#25 remains responsible for broader UTC schema standardization.

Tenant roles cannot acquire `Platform.ReferenceData.Manage`. Normal tenant
Application dispatch denies reference-data mutations. Platform tooling must obtain
the library's separate `PlatformAccessContext` through both an explicit policy
decision and successful audit, and construct the explicit platform context overload.
No platform capability is registered for ordinary API requests. Even that capability
does not allow runtime raw SQL or query-filter bypass.

## Staff projection and transactional publication

The v1 staff snapshot carries organisation ID, immutable membership ID, event ID,
monotonically increasing revision, active/tombstone state and display data. Source
staff mutation and outbox row commit together. Publication failure leaves a pending
row; the hosted retry worker starts automatically and processes explicitly configured
organisation selections, each reauthorized against AuthManagement. Startup fails
without worker identity, credential and selections. Staff staging rejects organisations
without configured retry coverage, so a successful mutation cannot silently depend on
an absent scheduler. No platform tenant enumeration or fabricated membership is used.

Each tenant batch has a ten-second cancellation budget and at most 100 records;
each broker wait is limited to two seconds. Cycles run every five seconds, backing
off to at most thirty seconds on cycle/access failures. Credentials are reread each
cycle. Pending records survive restart; failed attempts update the existing audit
timestamp and move behind older pending work to avoid starvation. Malformed records
remain pending and observable while other records proceed. Recovery continues
automatically rather than permanently abandoning tombstones after a retry limit.
Delivery may repeat after publication but before its acknowledgement commits.

The `AdminAreaManagement.StaffOutbox` meter exposes publication outcomes, pending
age and retry-cycle outcomes. Warning logs omit payloads, identifiers and exception
details. No persistence model or migration change is required for this retry path.

ClientManagement creates a new scope for every delivery and authorizes its worker
identity against AuthManagement. The event organisation is selection only, not proof.
Before applying a new revision it also verifies the target membership belongs to
that organisation. Active snapshots require active membership; tombstones can target
inactive memberships in the same organisation. Already-applied/older revisions
perform no write and do not require reactivating a revoked membership to acknowledge.
Duplicate deliveries and older revisions do not change the latest projection;
equal revisions with different event IDs fail, and tombstones cannot be undone by
older events. Membership links cannot be changed after persistence. Username and
display-name changes do not change security assignment. The legacy event handler
rejects messages that lack the versioned membership contract.

## Deployment prerequisites and migration procedure

1. Back up and rehearse against a representative PostgreSQL copy. Stop old staff
   producers/consumers and drain or quarantine legacy messages before switching
   the versioned contract. This is a coordinated rollout, not a rolling compatibility
   promise for old producers.
2. Complete the reviewed issue #43 organisation mapping first. Reconcile any
   existing duplicate staff usernames through the established issue #43 procedure.
3. Supply `__StaffMembershipMap` independently in each operational database with
   columns `LocalId integer`, `OrganisationId uuid`, `OrganisationMembershipId uuid`.
   `LocalId` means AdminArea StaffMembers.Id or ClientManagement SocialWorkerId.
   The mapping must come from authoritative verified memberships, not usernames.
   Each existing row requires exactly one non-empty mapping in the correct
   organisation; duplicate organisation/membership pairs abort migration.
4. If AuthManagement has legacy `Member` assignments, supply the explicitly approved
   `__MembershipRoleMap` (`MembershipId uuid`, `OrganisationId uuid`, `RoleCode text`).
   Only LimitedViewer, LimitedEditor, Viewer, Contributor or Editor are permitted for
   this conversion. Missing, ambiguous or unknown mappings abort. Verified baseline
   Owner/Admin IDs retain their identities. The legacy Member row is retained but
   ceases to be a system permission set.
5. Run migrations using the design-time factories with
   `ZEKA_MIGRATION_CONNECTION` supplied securely. Select `DeploymentDbContext` for
   AdminArea/ClientManagement and `AuthDbContext` for AuthManagement. No runtime
   `EnsureCreated`, `Migrate` or privileged deployment context is wired into APIs.
6. Supply the explicit typed authentication settings described in
   `issue-44-authentication.md`. Legacy shared SigningKey configuration is rejected.
   AuthManager alone receives host-supplied signing and retiring-key references;
   validators receive public keys only through the configured HTTPS JWKS URI.
   Configure `TenantAuthorization:AuthManagementUrl` as an HTTPS base URL.
7. Configure the consumer's `TenantWorker:SubjectId` and securely provided,
   renewable `TenantWorker:BearerToken`; the identity needs current active
   memberships with ManageStaffProfiles in its authorized organisations. Token
   rotation belongs to the worker host/operator. AdminArea additionally requires
   `TenantWorker:OrganisationIds` (at most 100 explicit non-empty GUID selections).
   Every organisation that produces staff events must be enrolled before mutation.
   The API-hosted worker supplies retry scheduling; no external scheduler is required.
8. Verify backfills, row counts, preserved relationships and pending-outbox
   delivery before enabling traffic. Automatic down migration deliberately raises
   an exception; recovery requires a reviewed backup/reconciliation procedure.

No live database migration or deployment was performed during implementation.

## Explicit security review and evidence

Review covered identity/selection separation, current permission evaluation,
membership immutability, SQL assignment predicates, tenant classifications and
composite relationships, bypass rejection, publication atomicity, replay ordering,
target membership ownership and migration failure/rollback behavior. PostgreSQL tests exercise these controls;
reflection tests detect missing Application policies and forbidden layer references.
JWT tests reject wrong signatures, issuers, audiences and expired tokens. HTTP
tests cover 401/403/503 translation, malformed/unknown contracts, one safe retry,
retry exhaustion, overall timeout, caller cancellation and redacted telemetry.

The full solution regression evidence includes PostgreSQL migrations, client-detail
projections, explicit membership immutability and the real HTTP bearer contract
with Identity cookie defaults present. All three EF models match their migrations.
The original reviewed baseline passed 327 tests. Post-review Release evidence is
recorded in `issue-44-review-corrections.md`, using SDK 8.0.129 and PostgreSQL 17
test containers.
Deterministic secrets scans passed. Sonar agentic analysis skipped all requested
files because Vortex is unavailable on the configured connection; it provides no
quality-gate evidence for this change.

Outstanding deployment/security follow-up: rotate the embedded legacy credential
in the external authentication package/issuing environment; provide the new runtime
authentication environment configuration; obtain reviewed production backfills; configure outbox
worker coverage and credential renewal; complete issues #45/#46 before claiming production
isolation/readiness. Restore also reports the existing AutoMapper 13.0.1 advisory
GHSA-rvv3-g6hj-g44x, which was not upgraded in this issue.

## Acceptance criteria mapping

| Issue #44 criterion | Implementation and executable evidence |
| --- | --- |
| Immutable scoped context without HTTP | Released TenantContextScope; TenantAuthorizationTests, parallel database scopes and per-delivery consumer scopes |
| Selection does not authorize | CurrentTenantAccessResolver and Application pipeline; current-state revocation, unknown permissions and HTTP credential tests |
| Every tenant EF entity scoped or rejected | Released model convention and both model-classification conformance tests |
| Missing/conflicting context fails closed | Both TenantEnforcementTests and HTTP selection-conflict tests |
| No forged inserts or tenant-ID mutation | Four SaveChanges overload tests, immutable membership tests and preserved composite foreign keys |
| Explicit detached/bypass/job/event behavior | Detached concurrency tests, raw SQL/bulk/filter rejection, guarded retry command, outbox rollback/retry and consumer replay/ownership tests |
| No scope leakage | Parallel scopes under reused pooled connections and independent consumer deliveries |
| Provider/HTTP-independent inner layers | Abstractions package, application-owned authorization ports, transport adapters and forbidden-assembly conformance tests |

## Accepted authentication replacement

Ubongo's Authentication Token Issuer and Validation contract was accepted on
2026-09-11 and implemented against `4d2db324f5da9dbcd2099266414b87c2c02bc92c`.
Shared HS256 validation has been removed. AuthManager is the replaceable RS256
issuer, and all three APIs use the same validator-only technical component and
environment audience. JWKS is the only downstream key distribution path.

The replacement adds no token/login/refresh endpoint, token exchange, On-Behalf-Of,
gateway transformation, identity infrastructure, workload/Azure identity, RLS,
credential provisioning, or authorization-policy change. Startup configuration,
readiness, key publication/rotation, generic 401/503 failures, bounds and verification
are documented in [issue-44-authentication.md](issue-44-authentication.md).
