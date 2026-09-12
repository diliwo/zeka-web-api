# Issue #44 — accepted RS256 authentication replacement

Baseline: `4d2db324f5da9dbcd2099266414b87c2c02bc92c`.
Authority: accepted Ubongo Authentication Token Issuer and Validation (2026-09-11),
including its bounded implementation contract, and accepted ADR-001/002/003.
Both remote refs were fetched and verified unchanged before implementation:
`origin/develop = 4bc9bc1e5b99ee49e6ae0d1f464debdd8ef24dde` and the issue branch
at the baseline above.

## Implementation boundary

- `Shared/Zeka.Authentication` supplies one typed validation configuration, a
  public-only bounded JWKS cache, framework RS256 token validation, generic HTTP
  failure translation, redacted diagnostics and a JWKS readiness check.
- The three API `TenantAuthentication` registrations use that component. The
  shared project contains no issuer interface, private-key provider or signing
  reference. Downstream startup rejects issuer configuration.
- AuthManager Infrastructure owns replaceable `ISigningKeyProvider`,
  `IAccessTokenIssuer`, and `IPublicJwksPublisher` boundaries. The supplied
  file adapter reads existing host-protected PEM references; it never generates,
  provisions or writes a credential. Only AuthManager composes it.
- AuthManager publishes `GET /.well-known/jwks.json` and
  `GET /health/authentication`. There is no new login, token or refresh endpoint.
  The signing primitive is available to later separately approved authentication flows.

Cryptographic validation and JWK parsing use Microsoft's
[JsonWebTokenHandler validation](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/wiki/ValidatingTokens)
and IdentityModel `JsonWebKeySet`; the API uses the supported
[JwtBearer token-handler extension](https://source.dot.net/Microsoft.AspNetCore.Authentication.JwtBearer/JwtBearerOptions.cs.html).
No signature algorithm is implemented here. A small explicit cache bounds trusted
metadata lifetime without an unlimited last-known-good fallback.

## Typed environment configuration

The `Authentication` section accepts only these scalar fields. All are required.
Unknown fields (including legacy `SigningKey`), nested/duplicate audience shapes,
missing values, malformed times and contradictory bounds fail startup with a
redacted error. Normal environment-provider overrides remain supported.

| Field | Meaning and enforced bound |
| --- | --- |
| Issuer | Exact absolute HTTPS issuer identifier; no user info, query or fragment |
| Audience | Exactly one explicit environment-specific audience; non-empty, no whitespace/comma, at most 256 characters |
| JwksUri | Exact absolute HTTPS JWKS URL; no user info, query or fragment |
| ClockSkew | Explicit TimeSpan, zero through five minutes |
| JwksRefreshInterval | Explicit TimeSpan, five seconds through one hour |
| JwksMaximumStaleness | Explicit TimeSpan, at least the refresh interval and at most 24 hours |

For example, an environment may choose a 30-second clock skew, five-minute refresh
interval and fifteen-minute maximum staleness. These are examples, not defaults or
deployed settings. Production configuration must name its own issuer, audience and
JWKS endpoint.

AuthManager additionally binds `AuthenticationIssuer`:

| Field | Meaning and enforced bound |
| --- | --- |
| ActiveSigningKeyReference | Fully qualified path to an existing host-protected RSA private PEM; no key material in configuration |
| ActiveKeyId | Non-empty kid, at most 128 characters |
| AccessTokenLifetime | Explicit positive TimeSpan, at most one hour |
| RetiringKeys | Zero to fifteen previous public-key entries |
| RetiringKeys[].KeyId | Unique non-empty kid, distinct from the active kid |
| RetiringKeys[].PublicKeyReference | Fully qualified path to an existing public-only RSA PEM on the issuer |
| RetiringKeys[].LastIssuedAtUtc | Explicit last issuance boundary for that previous key |
| RetiringKeys[].RemoveAfterUtc | At least LastIssuedAtUtc + one-hour maximum lifetime + clock skew, and no more than 24 hours after LastIssuedAtUtc |

Retirement uses the fixed one-hour issuance ceiling, not a possibly shorter new
configured token lifetime. This conservatively protects tokens from prior deployments.
Changing that ceiling needs coordinated contract review. References and rotation
state are issuer-only, never downstream key distribution; downstream public material
comes exclusively from JWKS. Options string representations are redacted.

No environment setting, credential, repository secret or deployment resource was
provisioned or changed in this implementation.

## JWKS validation and failure behavior

RS256 is fixed, not an environment-selected algorithm. RSA keys must be at least
2048 bits. A token must have one string kid and one exact issuer/audience; missing
kid, duplicate header fields, untrusted key-selection headers, malformed tokens,
unsupported algorithms (including HS256/none), signature and lifetime failures
are generic 401. The framework validates the signature, issuer, audience and lifetime
using exactly the matching trusted public key. It never tries every key or falls
back to a symmetric secret.

JWKS transport is HTTPS-only, does not follow redirects or use cookies, and
permits one request with a two-second total budget and 64 KiB maximum document
size. Up to sixteen unique RSA signing keys are accepted. Private JWK parameters,
ambiguous duplicate fields/identifiers and unsupported key shapes never establish
trust. An empty but valid trusted JWKS rejects the token with 401 and fails readiness.

The cache is single-flight and uses monotonic elapsed time. It refreshes on demand
after the configured interval, or for an unknown kid, subject to a global five-second
minimum between attempts. An unknown kid can cause at most one refresh per validation;
random-kid traffic cannot cause an unbounded refresh storm. No per-kid negative
cache or unbounded attacker-controlled state is kept.

- Successful trusted discovery without a matching key: generic 401.
- Refresh outage with a matching cached key inside maximum staleness: validation may continue.
- Outage without a usable matching non-stale key: generic 503.
- Malformed/unusable discovery without a usable cached key: generic 503.
- At maximum staleness, cached keys are unusable. There is no indefinite stale-key fallback.
- JWT validation proves identity only. All live membership, permission and resource checks
  remain unchanged, and the original bearer token is still forwarded unchanged.

Responses omit detailed errors and discovery/key information and use no-store.
The named `authentication-jwks` readiness health check is registered in each API.
Existing hosts can include it in their readiness endpoint through HealthCheckService;
this change does not add downstream business endpoints.

## Publish-before-use and rotation

The active key is not usable for issuance until its public JWKS response completes
successfully. Preparing a document alone does not authorize signing. The endpoint
confirms publication after a successful response, and the issuer compares the
current private key's public fingerprint with that confirmation before every issuance.
A restart requires fresh publication. Replacing key material behind a reference
invalidates readiness/issuance until the replacement is published.

For a planned rotation, the host supplies the new active key/reference and includes
the previous key's public reference plus its last-issuance and removal boundary in
the retiring set. The issuer publishes active and unexpired retiring public keys.
Serve the new JWKS before using the issuer primitive. Retiring keys remain published
through the enforced overlap and are omitted at their configured retirement time.
Validators discover removal on their next bounded refresh; prior tokens have reached
their maximum allowed lifetime plus skew by the removal boundary.

If the private key is missing, malformed, public-only, weak, or unpublished, issuer
readiness is 503 and issuance fails with AuthenticationAuthorityUnavailableException,
the stable unavailable result for the primitive. No ad-hoc key or HS256 fallback exists.
The provider's private handles are short-lived and disposed; signing-provider caching
is disabled so a disposed private handle cannot be reused.

These are host-supplied rotation inputs, not a key-management or provisioning platform.
No token exchange, On-Behalf-Of, gateway transformation, workload/Azure identity or
ADR-005 implementation was introduced.

## Diagnostics

`Zeka.Authentication` publishes `authentication.outcomes` and an
`authentication.validate` activity with only authenticated/invalid/unavailable outcomes.
`Zeka.Authentication.Issuer` publishes `authentication.issuer.outcomes` and an
`authentication.issue` activity with bounded publication/issuance/failure outcomes.
Validator warning logs contain only the reason class. JWKS HttpClient logging is
disabled, framework validation exceptions are replaced with redacted exceptions,
and bearer challenges omit error details.

Tokens, subjects, organisation IDs, kids, key material, key references and discovery
exceptions are not diagnostic dimensions. No telemetry vendor/exporter was added.

## Verification

Final commands, totals and changed-file review are recorded below.
Tests use ephemeral in-memory RSA keys; the file-provider test creates and removes
only its own unique temporary fixture file. No test uses a host credential.

The shared validator test source is compiled separately into each service's existing
Infrastructure.IntegrationTests project. A narrowly scoped AuthManager conformance
test compiles the exact two downstream registration source files to validate one
issuer-produced token across all three registrations, without referencing another
service's Application, Domain or persistence assemblies.

Existing Issue #44 permission, current-access, assignment, staff projection/outbox,
EF enforcement, migration and tenant-isolation production files remain unchanged.
No database model, migration or public staff-event contract was modified.

### Final Release evidence — 2026-09-11

SDK 8.0.129; PostgreSQL 17 Testcontainers using the local Docker engine.

```text
dotnet build zeka-web-api.sln -c Release --no-restore --verbosity quiet
Exit 0: build succeeded, 0 errors. Complete Release warning evidence:
4 pre-existing warnings total: 2 NU1903 AutoMapper advisories,
plus gateway analyzer warnings ASP0013 and ASP0014.

dotnet test zeka-web-api.sln -c Release --no-build --no-restore
  --logger "trx;LogFileName=issue44-rs256-release.trx" --verbosity quiet
Exit 0: 489 passed, 0 failed, 0 skipped, across the original 12 test projects.
```

| Service | Domain unit | Application unit | Application integration | Infrastructure integration | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| AdminAreaManagement | 60 | 42 | 4 | 116 | 222 |
| AuthManager | 23 | 3 | 1 | 72 | 99 |
| ClientManagement | 32 | 16 | 15 | 105 | 168 |
| Total | 115 | 61 | 20 | 293 | 489 |

The baseline's 393 cases remain green. The replacement adds 96 authentication
cases: 29 validator contract cases per service, plus nine issuer contract cases.
The existing signature/issuer/audience/lifetime and forwarded tenant-access cases
were adapted from HS256 to RS256 without changing their authorization assertions.

Focused authentication commands were also run against each service with filters
for BearerValidationTests, IssuerContractTests, TenantAccessEndpointTests and
TenantHttpTests. The final full run includes the subsequently added empty/private/
duplicate JWKS and file-provider/typed-registration cases.

| Required evidence | Passing tests |
| --- | --- |
| One issuer-produced common-audience token across three APIs | IssuerContractTests.One_issuer_token_is_accepted_by_all_three_actual_api_registrations |
| Forwarded token reaches current-access endpoint; authorization outcomes preserved | TenantAccessEndpointTests.Tenant_contract_uses_bearer_even_when_identity_cookie_is_the_default_scheme, plus unchanged downstream access-adapter suites |
| HS256, none, substitution, wrong signature/issuer/audience/expiry and missing kid | ValidatorContractTests.Invalid_tokens_are_generic_401, compiled separately for all three APIs |
| Unknown kid refresh and refresh-storm bound | Unknown_kid_refreshes_once_then_succeeds_or_returns_401_after_trusted_discovery |
| Outage, usable cache, maximum staleness and recovery | Outage_allows_only_non_stale_matching_cache_and_recovers; Discovery_timeout_is_bounded |
| Successful empty JWKS versus unusable JWKS | Empty_trusted_jwks_rejects_unknown_key_as_401_and_fails_readiness; Unusable_jwks_never_establishes_trust |
| Publish-before-use, replaced private handles and overlap/retirement | Issuer_refuses_unpublished_keys_and_key_replacement_requires_republication; Rotation_retains_previous_public_key_for_maximum_lifetime_and_skew_then_retires_it (including old/new token validation) |
| Downstream cannot sign | Public_validation_key_cannot_sign_and_no_private_material_is_distributed; cross-API composition has no issuer or private-key provider |
| Startup/configuration/readiness | Invalid_or_legacy_configuration_fails_startup_without_echoing_values; Issuer_configuration_is_required_bounded_redacted_and_forbidden_downstream; Missing_signing_material_fails_readiness_and_issuance_without_fallback; Actual_jwks_endpoint_exposes_only_public_fields_and_controls_readiness |
| Provider behavior and no credential mutation | Host_supplied_private_file_is_read_only_and_issuance_uses_the_supplied_key; Public_only_or_weak_signing_material_is_rejected; Typed_issuer_registration_shares_one_publication_and_signing_boundary |
| Redacted diagnostics | Redacted_diagnostics_distinguish_invalid_and_unavailable verifies real logs, Meter and ActivitySource |
| Existing Issue #44 behavior | All permission, membership, access-result, assignment, outbox recovery, PostgreSQL migration/isolation and scope-leakage tests in the complete suite |

Raw local evidence is retained in each test project's ignored
`TestResults/issue44-rs256-release.trx`, and in the temporary-directory logs
`issue44-rs256-build.log` and `issue44-rs256-release-test.log`.
The complete Release warning count is four pre-existing warnings: two AutoMapper
13.0.1 advisories (NU1903, GHSA-rvv3-g6hj-g44x), plus gateway analyzer warnings
ASP0013 (`Program.cs:15`) and ASP0014 (`Program.cs:39`). The retained incremental
build log reports only the two package advisories; it does not represent the
complete Release warning evidence. All four are outside this bounded replacement.
This is not a warning-free build.

### Bounded delivery correction — 2026-09-12

Reviewed baseline: `53403a8788897faf73fda5356b1af124e028a367`.
Scope: the user's post-review delivery correction for Issue #44 only.
Ubongo was pulled with `--ff-only` (already current); the accepted Authentication
Token Issuer and Validation decision and ADR-002 testing responsibilities were
consulted. Authentication architecture and cryptographic controls remain accepted.

- Removed obsolete `Authentication:AuthBaseAddress` from the tracked AuthManager
  and ClientManagement base settings. The strict `AuthenticationOptions` binder
  and all production C# files are unchanged.
- Added `AuthenticationStartupTests` to each service's existing infrastructure
  integration project, using the linked `RealHostStartupTests` helper. No new
  package or production test hook is required.
- Each case launches the actual compiled API entry point with Kestrel on an
  ephemeral loopback port, using an exact copy of its tracked `appsettings.json`
  and process-local environment overrides supplied before `CreateBuilder` runs.
  Developer environment configuration and user secrets are excluded. The tests
  neither rebuild a partial host nor replace service registrations.
- Each service has one successful startup case and two fail-closed cases:
  reintroducing `AuthBaseAddress`, or adding `UnexpectedKey`, must terminate startup
  with `Invalid authentication configuration.` without echoing the test value.
  Existing negative validator coverage remains unchanged.
- AuthManager's successful case also verifies the real JWKS endpoint returns
  exactly the ephemeral fixture's public key. Its temporary signing fixture and
  isolated host directory are deleted after the child process exits. ClientManagement
  must serve an HTTP response from its actual pipeline. These are configuration
  and HTTP-startup checks, not broker, database, or deployment-readiness claims.

The only changed components are the two base settings, the two startup-test classes,
their project source links, the shared startup-test helper, and this evidence report.
RS256/JWKS, issuer/audience, kid/rotation, authorization, membership, assignment,
outbox, EF/migrations, tenancy, workload/Azure identity and RLS/runtime roles are
unchanged. No host credential or deployment setting was changed.

Verification uses SDK 8.0.129 and the existing local Docker engine. The focused
AuthManager authentication/startup run passed 42 tests; ClientManagement passed 51.
The initial new AuthManager assertion compared JSON property order; it was corrected
to structural JSON equality after confirming the real host returned the expected
public JWKS. No production behavior or existing assertion was changed.

The complete Release build (`dotnet build zeka-web-api.sln -c Release --no-restore
--verbosity quiet`) exited 0 with 0 errors. This run recompiled additional untouched
projects and reported **264 warnings**, including the four warnings identified above;
it must not be represented as a four-warning run. The full log is retained at
`%TEMP%/issue44-delivery-build.log`. No warning was suppressed or repaired out of scope.

The initial full Release test run passed 486 cases and failed nine AuthManager cases
during Testcontainers Docker endpoint discovery (`npipe://./pipe/docker_engine`
ping cancellation). All six new startup cases passed. This was an infrastructure
availability failure before the affected database fixtures could initialize;
no application assertion or production code was changed to address it.

All nine affected cases passed on an unchanged targeted retry after Docker responded.
The final complete Release run used sequential project execution to reduce concurrent
Docker discovery/startup pressure:

```text
dotnet test zeka-web-api.sln -c Release --no-build --no-restore -m:1
  --logger "trx;LogFileName=issue44-delivery-release-final.trx" --verbosity quiet
Exit 0: 495 passed, 0 failed, 0 skipped, across all 12 test projects.
```

| Service | Domain unit | Application unit | Application integration | Infrastructure integration | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| AdminAreaManagement | 60 | 42 | 4 | 116 | 222 |
| AuthManager | 23 | 3 | 1 | 75 | 102 |
| ClientManagement | 32 | 16 | 15 | 108 | 171 |
| Total | 115 | 61 | 20 | 299 | 495 |

Final raw results are retained in each project's ignored
`TestResults/issue44-delivery-release-final.trx` and
`%TEMP%/issue44-delivery-release-final-test.log`. The initial failed run remains
separately available as `issue44-delivery-release.trx` and
`%TEMP%/issue44-delivery-release-test.log`; the targeted recovery log is
`%TEMP%/issue44-delivery-docker-retry.log`.

The acceptance evidence is therefore: both obsolete tracked keys removed; both
real hosts start with valid RS256/JWKS environment configuration; both hosts reject
obsolete and arbitrary unknown authentication keys; the strict binder and accepted
cryptographic controls are unchanged; complete warning evidence is corrected;
focused tests, the complete Release build and final complete suite pass.
Final scope/security review, deterministic secrets scans of all eight changed files,
and `git diff --check` passed. Existing compiler/advisory/analyzer warnings and the
transient Docker discovery failure are recorded above and left outside the code change.
No push, PR, merge, Issue #44 closure, credential mutation or ADR-005 work was performed.
The local commit SHA and post-commit working-tree status are reported in the handoff.

### Final test/delivery correction — 2026-09-12

Reviewed baseline: `30dc53230c208b7d556d42ac339276efc8875caa`.
Chief accepted the production authentication/authorization implementation. This
correction changes only five files: the ClientManagement startup fixture, its shared
process-test helper, `.github/workflows/mvp-validation.yml`, the two duplicate using
directives in AdminAreaManagement `Program.cs`, and this report. No package references,
production authentication/authorization logic, persistence, deployment configuration,
credentials, or repository settings changed. Ubongo was already current; ADR-002's
integration-test responsibilities and the accepted authentication boundary are preserved.

ClientManagement startup is now self-contained:

- Each xUnit case owns a fresh Testcontainers network and `rabbitmq:3.13.7-alpine`
  broker. It waits for RabbitMQ's startup-complete signal, then a successful
  `rabbitmq-diagnostics -q check_port_connectivity` result.
- The production adapter accepts only a hostname and uses AMQP port 5672. The real
  compiled API therefore runs in `mcr.microsoft.com/dotnet/aspnet:8.0.30` on that
  private network, with the broker's network alias. No broker host port is published
  or selected, and no developer broker is reused. The API's HTTP port is mapped to
  a random host port. This follows the existing Testcontainers approach without
  introducing a package or changing production configuration binding.
- The same shared test method still executes the actual entry point and asserts
  startup, real HTTP response, nonzero exit on unknown configuration, and redacted
  error text. Only process preparation/address translation gain test extension points;
  no production registration or service is mocked or replaced. The tracked base
  `appsettings.json` and isolated environment overrides remain the configuration inputs.
- The successful case additionally queries this broker and requires one consumer on
  the unique test queue. A listening HTTP host with a failed subscriber is insufficient.
  API and broker containers and their network are disposed after each case, including
  failure paths. Fresh broker creation and cleanup are visible in the startup log.

The initial fixture attempt ran a diagnostics command before broker initialization
completed, causing a fresh-container initialization race. Waiting for the server's
startup-complete signal before diagnostics resolved it. No host credential, existing
broker, or production implementation was modified.

Required PR validation restores and runs the **entire ClientManagement
Infrastructure.IntegrationTests project**, with no filter, in the existing Docker-enabled
`postgresql` job. Testcontainers provisions and tears down its dependencies. The job ID,
check name (`PostgreSQL Testcontainers`), and required `MVP Validation` aggregate are
preserved. A failure in this suite sets the existing nonzero step result and fails the
aggregate. No manually provisioned RabbitMQ service or CI credential is required.
The workflow passes actionlint 1.7.12, including its bundled shell checks.
Its exact integration-test `run` block was extracted unchanged and executed locally
with Bash `--noprofile --norc -e -o pipefail`: exit 0, **202 tests passed** across
the four listed projects (75 AuthManager infrastructure, 4 AdminAreaManagement
application integration, 15 ClientManagement application integration, and all 108
ClientManagement infrastructure cases). Evidence is retained in
`%TEMP%/issue44-required-test-step.sh` and `%TEMP%/issue44-required-test-step.log`.
This proves local execution of the required command block; no hosted GitHub Actions
run is claimed and no PR was opened to trigger one.

Verification with SDK 8.0.129:

| Check | Result |
| --- | --- |
| Fresh ClientManagement real-host cases | 3 passed; test-owned broker/API/network, including actual broker consumer |
| Focused AuthManager startup/authentication | 42 passed |
| Focused AdminAreaManagement authentication | 48 passed |
| Focused ClientManagement startup/authentication | 51 passed |
| Complete Release suite, all 12 projects | 495 passed, 0 failed, 0 skipped |
| Complete Release build | Exit 0; **23 warnings, 0 errors** |

```text
dotnet build zeka-web-api.sln -c Release --no-restore --verbosity quiet
dotnet test zeka-web-api.sln -c Release --no-build --no-restore -m:1
  --logger "trx;LogFileName=issue44-final-test-delivery.trx" --verbosity quiet
```

The 23 build warnings comprise two NU1903 advisories, one ASP0013, one ASP0014,
four CS8600, three CS8602, two CS8603, four CS8604, five CS8625, and one xUnit2009.
This is the exact count for this run, distinct from the historical runs above.
Only the two authorized duplicate using directives were removed; remaining warnings
were not suppressed or repaired outside scope.

Raw evidence is retained in `%TEMP%/issue44-final-startup-retry.log`,
`%TEMP%/issue44-final-focused.log`, `%TEMP%/issue44-final-build.log`,
`%TEMP%/issue44-final-release-suite.log`, and each project's ignored
`TestResults/issue44-final-test-delivery.trx`.

The inventory remains **755 tracked files**. Generic scans are scoped to the current
contents of the **280 Issue #44/correction delta files** relative to develop baseline
`4bc9bc1e5b99ee49e6ae0d1f464debdd8ef24dde`, including this five-file correction.
Both `sonar analyze secrets` and Gitleaks 8.30.1 (`dir`, default rules, `--redact`)
pass this delta. The isolated scan snapshot excludes the known develop Kubernetes
Secret. Delta checks also find zero private-key markers and zero key/certificate
artifacts. This is not a generic-scanner-clean claim about the complete repository.
The five-file correction also passes a separate delta scan. Final `git diff --check`
and scope review pass; the worktree is checked clean after the single commit and push.
The full 40-character SHA is provided in the handoff for Chief's remote review.
No PR, merge, Issue #44 closure, credential/repository-setting change, or ADR-005 work
is part of this delivery.

### Scope and security review

Exact changed-file review is limited to the authentication component, three
registrations, AuthManager issuer/JWKS composition, corresponding tests/project
references and documentation. Permission, current-membership, assignment, outbox,
EF, migration and tenant-isolation production paths are unchanged.

The current tracked-file count is **755** (`git ls-files`); this is an inventory
count, not a repository-wide generic-scanner result. The Issue #44 and correction
deltas pass generic secret scanning. Delta-only key checks find no private-key
markers or key/certificate artifacts. The production authentication component and
three registrations remain free of symmetric fallback. The staged `git diff --check`
passed. Post-commit cleanliness is reported with the commit SHA.

Chief identified a pre-existing Kubernetes Secret finding on `develop` in
`Deployments/k8s/zekadb/zekadb-secret.yaml`. It is outside Issue #44's delta and this
correction's scope. Its value was not inspected, reproduced, or modified. This report
does **not** claim that the complete repository tree is generic-scanner clean.
Remediation requires separate authorization; no credential value is included here.

The accepted architecture is implemented; no new architecture decision remains
within this contract. Deployment still requires independently authorized environment
configuration, protected key references and rotation inputs. These were not provisioned.
The original RLS and other production-readiness follow-ups remain outside Issue #44's
authentication replacement, and these results are not a deployment-readiness claim.

Stop boundary: one local commit only; no push, PR, merge, issue closure, credential
mutation, or ADR-005 work.

### Changed files

- [Docs/issue-44-authentication.md](../Docs/issue-44-authentication.md)
- [Docs/issue-44-tenant-enforcement.md](../Docs/issue-44-tenant-enforcement.md)
- [Services/AdminAreaManagement/AdminAreaManagement.API/AdminAreaManagement.API.csproj](../Services/AdminAreaManagement/AdminAreaManagement.API/AdminAreaManagement.API.csproj)
- [Services/AdminAreaManagement/AdminAreaManagement.API/TenantAuthentication.cs](../Services/AdminAreaManagement/AdminAreaManagement.API/TenantAuthentication.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj)
- [Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs](../Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs)
- [Services/AuthManager/AuthManager.API/AuthManager.API.csproj](../Services/AuthManager/AuthManager.API/AuthManager.API.csproj)
- [Services/AuthManager/AuthManager.API/Endpoints/IssuerEndpoints.cs](../Services/AuthManager/AuthManager.API/Endpoints/IssuerEndpoints.cs)
- [Services/AuthManager/AuthManager.API/Program.cs](../Services/AuthManager/AuthManager.API/Program.cs)
- [Services/AuthManager/AuthManager.API/TenantAuthentication.cs](../Services/AuthManager/AuthManager.API/TenantAuthentication.cs)
- [Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj](../Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj)
- [Services/AuthManager/AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs](../Services/AuthManager/AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs)
- [Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerOptions.cs](../Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerOptions.cs)
- [Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerRegistration.cs](../Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerRegistration.cs)
- [Services/AuthManager/AuthManager.Infrastructure/Authentication/SigningKeyProvider.cs](../Services/AuthManager/AuthManager.Infrastructure/Authentication/SigningKeyProvider.cs)
- [Services/AuthManager/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs](../Services/AuthManager/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs)
- [Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj](../Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj)
- [Services/AuthManager/Tests/Infrastructure.IntegrationTests/IssuerContractTests.cs](../Services/AuthManager/Tests/Infrastructure.IntegrationTests/IssuerContractTests.cs)
- [Services/AuthManager/Tests/Infrastructure.IntegrationTests/TenantAccessEndpointTests.cs](../Services/AuthManager/Tests/Infrastructure.IntegrationTests/TenantAccessEndpointTests.cs)
- [Services/ClientManagement/Client.API/ClientManagement.API.csproj](../Services/ClientManagement/Client.API/ClientManagement.API.csproj)
- [Services/ClientManagement/Client.API/TenantAuthentication.cs](../Services/ClientManagement/Client.API/TenantAuthentication.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/BearerValidationTests.cs)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj)
- [Services/ClientManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs](../Services/ClientManagement/Tests/Infrastructure.IntegrationTests/TenantHttpTests.cs)
- [Shared/Authentication.TestSupport/AuthenticationFixture.cs](../Shared/Authentication.TestSupport/AuthenticationFixture.cs)
- [Shared/Authentication.TestSupport/ValidatorContractTests.cs](../Shared/Authentication.TestSupport/ValidatorContractTests.cs)
- [Shared/Zeka.Authentication/AuthenticationOptions.cs](../Shared/Zeka.Authentication/AuthenticationOptions.cs)
- [Shared/Zeka.Authentication/BearerAuthentication.cs](../Shared/Zeka.Authentication/BearerAuthentication.cs)
- [Shared/Zeka.Authentication/JwksTrust.cs](../Shared/Zeka.Authentication/JwksTrust.cs)
- [Shared/Zeka.Authentication/Zeka.Authentication.csproj](../Shared/Zeka.Authentication/Zeka.Authentication.csproj)
- [zeka-web-api.sln](../zeka-web-api.sln)
