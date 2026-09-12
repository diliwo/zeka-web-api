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

## Verification — corrected portable harness

Reviewed remote baseline: `5e65d3231f03044ab40e5092e7b0f23866890128`.
Verification date: 2026-09-12. This section replaces the earlier environment-dependent
execution claims; the results below were rerun against the corrected implementation.
The accepted production authentication/authorization implementation and required CI
wiring were not changed.

### Authorized correction

- `Services/ClientManagement/Tests/Infrastructure.IntegrationTests/AuthenticationStartupTests.cs`
  passes the Docker endpoint already resolved by Testcontainers to the child CLI with
  `--host`. Sanitizing HOME and the child environment can no longer make the CLI
  independently select a default socket or context.
- Unix endpoint URIs pass through unchanged, including user-specific rootless socket
  paths. Windows named-pipe URIs are converted from Docker.DotNet's
  `npipe://./pipe/...` representation to the equivalent Docker CLI UNC representation,
  `npipe:////./pipe/...`. No socket path is hard-coded in the harness.
- Explicit `DOCKER_CONFIG`, `DOCKER_TLS`, `DOCKER_TLS_VERIFY`, and `DOCKER_CERT_PATH`
  references are preserved for the CLI only. Docker context selection is replaced
  by the resolved endpoint; application configuration remains sanitized and these
  transport references are not added to the API container's environment.
- Removed only the remaining duplicate `using ClientManagement.API` from the existing
  `Services/ClientManagement/Client.API/Program.cs` path. No host registration,
  middleware, authentication, authorization, or other production behavior changed.

Docker's documented [CLI endpoint and configuration precedence](https://docs.docker.com/reference/cli/docker/)
explains why supplying the resolved host is necessary after environment sanitization.
Ubongo remained current; the accepted authentication boundary and ADR-002 integration
responsibilities are preserved. No package, CI job/check identity, or repository setting changed.

### Active endpoint and cleanup evidence

The real-host suite ran with the active Docker endpoint explicitly supplied to
Testcontainers. The selected context was `default`, on the existing Rancher Desktop
Docker engine (29.5.3). Its Windows endpoint was `npipe:////./pipe/docker_engine`,
expressed as `npipe://./pipe/docker_engine` for Testcontainers. The child CLI connected
to that same pipe using the converted URI and executed the real API successfully.
No `/var/run/docker.sock` assumption or manually provisioned RabbitMQ was used.
This is an executed Windows named-pipe result; a separate rootless daemon run is not
claimed. Rootless Unix URIs are passed directly from Testcontainers rather than reconstructed.

All three real-host cases passed. The actual entry point, HTTP response, broker consumer,
unknown-key rejection, and redacted startup-error assertions are retained. Each case
still owns its private network, RabbitMQ container, and API container through the existing
xUnit/Testcontainers lifecycle. Production services are not replaced or mocked.

Cleanup was checked independently against the active Docker engine:

| Path | Execution evidence | Cleanup evidence |
| --- | --- | --- |
| Successful host and rejected configuration cases | 3 passed | Six fixture containers and three private networks absent from Docker; explicit disposal logged for every fixture resource |
| Intentional assertion failure while API and broker were live | 2 passed, 1 expected failure | The same six-container/three-network cleanup verified after failure |

For the failure probe, a temporary `Assert.True(false, "Intentional cleanup verification failure.")`
was inserted at the existing successful broker-consumer check. xUnit reached that assertion,
then the shared process `finally` and fixture disposal executed. A script restored the
original source in its own `finally`. The mutation was not committed. The corrected source
was rebuilt afterward and used for every final acceptance run below; the expected-failure
probe is separate from those totals.

The cleanup verifier collected created resource IDs, required explicit disposal records,
and queried containers/networks through the active endpoint to prove none remained. It also
checked the auxiliary Testcontainers container when present; no unrelated Docker resource
was stopped or changed.

Local evidence under `%TEMP%`:

- `issue44-portability-startup-corrected.log`
- `issue44-portability-intentional-failure.log`
- `issue44-portability-check-cleanup.ps1`
- `issue44-portability-success-cleanup.log`
- `issue44-portability-failure-cleanup.log`

### Required workflow and complete Release verification

The required four-project `run` block in `.github/workflows/mvp-validation.yml` was
extracted unchanged and executed with Bash `--noprofile --norc -e -o pipefail`, using
the active Docker endpoint and the corrected source. No filter or test skip was added.
All **202 expected tests passed**, with zero failures/skips:

| Required project | Passed |
| --- | ---: |
| AuthManager Infrastructure.IntegrationTests | 75 |
| AdminAreaManagement Application.IntegrationTests | 4 |
| ClientManagement Application.IntegrationTests | 15 |
| ClientManagement Infrastructure.IntegrationTests, including real-host startup | 108 |
| Total | 202 |

The complete Release suite then discovered and passed **495 tests across all 12 projects**,
with **zero failures and zero skips**:

| Service | Domain unit | Application unit | Application integration | Infrastructure integration | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| AdminAreaManagement | 60 | 42 | 4 | 116 | 222 |
| AuthManager | 23 | 3 | 1 | 75 | 102 |
| ClientManagement | 32 | 16 | 15 | 108 | 171 |
| Total | 115 | 61 | 20 | 299 | 495 |

```text
dotnet test zeka-web-api.sln -c Release --no-build --no-restore -m:1
  --logger "trx;LogFileName=issue44-portability-release.trx" --verbosity quiet
```

The required CI wiring, job/check names, and aggregate remain byte-for-byte unchanged
from the reviewed commit. Workflow lint passes with actionlint 1.7.12, including shell
checks. The required command block was executed locally; no hosted Actions run or new PR
is claimed.

Raw evidence: `%TEMP%/issue44-portability-required-step.sh`,
`%TEMP%/issue44-portability-required-step.log`, `%TEMP%/issue44-portability-release.log`,
and each project's ignored `TestResults/issue44-portability-release.trx`.

### Compiler diagnostic provenance against clean develop

The clean develop baseline is `4bc9bc1e5b99ee49e6ae0d1f464debdd8ef24dde`, confirmed
against remote `refs/heads/develop`. It was checked out separately with a clean tracked
worktree. Both that baseline and the corrected Issue #44 source were built with SDK
8.0.129 and the same complete, non-incremental Release command:

```text
dotnet build zeka-web-api.sln -c Release --no-restore --no-incremental --verbosity quiet
```

Both builds succeeded with zero errors. Compiler diagnostic provenance was compared by
**relative source path + CS diagnostic ID + diagnostic message**, removing duplicate
emissions and disregarding line/column shifts. The corrected Issue #44 build introduces
**zero compiler diagnostics relative to clean develop**. The remaining compiler diagnostics
have matching baseline identities; no warning suppression or unrelated repair was used.

Raw warning totals are not used as cross-build provenance. Package advisories and
framework/test-analyzer diagnostics are separate from this compiler-identity comparison;
this is not a warning-free-build claim.

Reproducible local evidence under `%TEMP%`:

- `issue44-portability-baseline-build.log`
- `issue44-portability-corrected-build.log`
- `issue44-portability-compare-diagnostics.ps1`
- `issue44-portability-baseline-diagnostics.txt`
- `issue44-portability-corrected-diagnostics.txt`
- `issue44-portability-introduced-diagnostics.txt` (empty)
- `issue44-portability-warning-provenance.log`

### Delta scans and scope

The inventory is **755 tracked files**, not a repository-wide generic-scanner result.
The current **280-file Issue #44/correction delta** relative to the clean develop SHA
above, and this **three-file portability correction** separately, pass generic secret
scanning with `sonar analyze secrets` and Gitleaks 8.30.1 (default rules, `dir`, `--redact`).
Delta key checks find zero private-key markers and zero key/certificate artifacts.
`git diff --check` passes.

Chief's pre-existing develop Kubernetes Secret finding in
`Deployments/k8s/zekadb/zekadb-secret.yaml` remains outside the delta and authorized scope.
Its value was not inspected, reproduced, or changed. No complete-tree generic-scanner-clean
claim is made; remediation requires separate authorization.

The accepted RS256/JWKS, issuer/audience/kid/rotation, membership/permission/resource
checks, assignment/outbox, EF/migrations/tenant isolation, RLS/runtime roles, and
workload/Azure identity remain unchanged. No credentials, secrets, unrelated files,
repository settings, or CI identity were changed. These tests are not a production-readiness
claim.

The delivery boundary is one clean local commit pushed to the existing Issue #44 branch.
The handoff supplies the full remote SHA and verifies a clean issue worktree. No PR, merge,
Issue #44 closure, or ADR-005 work is included.
