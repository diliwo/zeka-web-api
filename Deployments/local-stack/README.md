# Milestone 1 local stack — W1 candidate

This standalone Compose project is **zeka-m1**. It does not extend the historical
root Compose files, use fixed historical names, mount old data, execute init SQL
or apply migrations from application startup.

**Status: reviewable configuration; application startup and W1 acceptance held.**
Parsing is not a fresh install, runtime readiness, C1 acceptance or the W2 spike.

## Dependencies before startup

1. Zeka.Extensions.EventBus 1.2.0 accepts only RabbitMq:HostName and opens a
   connection eagerly. Username/password configuration is unsupported. Do not
   invent ignored keys or permit remote default-guest access. A bounded consumer
   correction requires its separate repository/publication boundary.
2. AdminArea and Client tenant migrations require public.__OrganisationTenantMap
   on a fresh database. The separate W2 scope must handle this prerequisite;
   never reuse the historical map. W1 does not change these migrations.
3. The repository pins SDK 8.0.129 with roll-forward disabled. Verify that exact
   SDK for host builds/tests. Do not silently use SDK 10 or change this pin.
4. W2 setup and protected HTTPS/signing/trust material must be admitted before
   starting the opt-in **applications** profile.

Before startup, verify the current owner-local session, exact Docker engine,
project/resource/port conflicts and Windows/WSL capacity. No alternate-engine
fallback, historical reset or assumption based only on a project label.

## Protected configuration interface

Set **ZEKA_M1_PRIVATE_DIR** to an absolute owner-protected directory **outside
this checkout and Docker build context**. It is a path, not a secret. Use the
approved protected, untracked delivery mechanism. No real material belongs in
tracked files, .env, build arguments or container environment values.

| Private relative file | Consumer |
| --- | --- |
| postgres/admin-password | New PostgreSQL administrator via POSTGRES_PASSWORD_FILE |
| rabbitmq/rabbitmq.conf | New broker; nondefault identity after consumer correction |
| auth/appsettings.LocalStack.json | Auth runtime configuration |
| auth/signing-key.pem | Auth-only signing-key reference |
| auth/browser-session-hash.key | Auth-only browser-session key reference |
| adminarea/appsettings.LocalStack.json | AdminArea runtime configuration |
| client/appsettings.LocalStack.json | Client runtime configuration |
| gateway/appsettings.LocalStack.json | Gateway configuration |
| trust/ca-bundle.pem | Admitted public CA trust bundle; no TLS bypass |
| build/github-token | Existing Dockerfile BuildKit restore secret only |

Each file is granted only to its consumer. File-based Compose secrets are
read-only bind mounts, not encrypted secret storage. Host ACLs and the daemon
remain trust boundaries. Do not assume uid/gid/mode remapping for file sources.

Standard WebApplication.CreateBuilder loads /app/appsettings.LocalStack.json
with environment **LocalStack**, avoiding Development user-secret loading and
Development-only business-write activation.

| API | DB configuration key | Restricted role | New database |
| --- | --- | --- | --- |
| Auth | ConnectionStrings:Default | zeka_auth_runtime | zeka_m1_auth |
| AdminArea | ConnectionStrings:ClientApiConnection | zeka_adminarea_runtime | zeka_m1_adminarea |
| Client | ConnectionStrings:ClientApiConnection | zeka_client_runtime | zeka_m1_client |

DB host is **postgres**, internal port5432. Administrator/owner/migrator
credentials never enter API configuration. Existing startup identity/RLS guards
remain active. AdminArea/Client require FileServerPath=/var/lib/zeka/files and
RabbitMq:HostName=rabbitmq; Client requires a project-specific EventBus:QueueName.
The current broker package has no credential keys.

Keep exact HTTPS Authentication issuer/JWKS/audience and trust bounds. Auth alone
receives AuthenticationIssuer and BrowserSession material. Their existing
absolute references use /run/secrets/auth_signing_key.pem and
/run/secrets/auth_session_hash_key. Downstream services receive no private key.
The gateway retains only its existing six explicit browser routes and policy/
certificate validation. No B2 route or TLS bypass is added. HTTPS edge/certificate
creation and full trust integration remain W5/C2.

## Non-destructive validation

From the repository root, with synthetic/protected inputs:

    docker --context default compose --project-name zeka-m1 -f Deployments/local-stack/compose.yaml --profile applications config --quiet

This parses without creating containers/networks/volumes. Do not publish resolved
secret-bearing configuration. Test fixtures belong outside the checkout.

After dependency admission, runtime operations must use this explicit
file/project/context. Named volumes preserve state on normal starts. Safe reset
is W4: verify exact owned IDs/labels first. No wildcard deletion, broad prune or
historical resource deletion is supplied.

## Health, networking and memory

PostgreSQL/RabbitMQ publish no host ports. APIs are internal. The gateway binds
only **127.0.0.1:19000** for C1 diagnostics; this is not C2 HTTPS authentication.
The historical5432/5672/15672 bindings are not reused.

| Method/path, LocalStack only | Result |
| --- | --- |
| GET /health/live |200; process liveness without dependency checks |
| GET /health/ready |200 only healthy;503 unhealthy/degraded; empty response |

Health middleware precedes business TLS/auth. Auth includes its existing issuer
readiness/current published-key fingerprint. Gateway checks its validated browser
policy, not live upstream/database/broker availability. Other environments and
business authorization/transport remain unchanged.

The Bash probe requires HTTP200, rejects redirects, emits no response body and
has a five-second Docker deadline. Verify Bash exists in the reused image;
binary/behaviour validation is reported separately. PostgreSQL uses pg_isready;
RabbitMQ uses its existing rabbitmq-diagnostics.

Configured maxima total **3.25GiB**: PostgreSQL1GiB, RabbitMQ512MiB, three APIs
512MiB each, gateway256MiB. This is not a reservation or allowance for build/VM
overhead. Recheck real capacity before startup; build sequentially. Per-container
logging is none to avoid retained raw exceptions/credentials; health exit status
is separate. No global logging policy changes.

## Separate W2 boundary

Admit a source-bound compatible CLI runner, new disposable credentials and exact
owned resources. Order: administrative database creation, matching role bootstrap,
restricted migrator plan/apply to latest, administrative post-bootstrap, versioned
manifest verification and runtime-role verification. Preserve exact first failures
and clean only spike-owned resources under the later bounded handoff.

Authored checks, parsing, builds, runtime operations and protected CI are separate
claims. None implies migration/API integration/C1 acceptance.

## Minimum dependency correction proposal (not executed)

The EventBus correction belongs in `diliwo/eventbus-dotnet-lib`, starting from
the source matching package 1.2.0 (75ab9f4d6c57ee37dd615ce4f32ef44f87698737).
Add protected-configuration username/password options and pass them to the
existing RabbitMQ ConnectionFactory; validate missing/partial configuration
without including values in messages. Preserve IRabbitMqConnection, connection
lifecycle and queue semantics. Cover option binding, nondefault credentials,
missing/partial credentials and sanitized failures with synthetic tests. Publish
a reviewed package version, then update only the two API consumers in a separately
authorized change. Do not enable remote guest access or invent ignored keys.

For local test execution, supply the repository-pinned SDK 8.0.129 through an
approved installation, or separately approve a compatible SDK-pin correction.
The reused Dockerfiles also select a different SDK version; their compatibility
with the disabled roll-forward pin has not been built or accepted. No SDK,
Dockerfile, dependency or framework upgrade is included here.

## Copy-ready diagnostic API contract

These are non-tenant, status-only probes, enabled only in LocalStack. They take
no body, query, cookies, bearer credential or organisation context. Once the
separate startup gates have passed, the gateway probes are:

    GET http://127.0.0.1:19000/health/live
    GET http://127.0.0.1:19000/health/ready

For a Bruno request, use the exact GET URL above with an empty body and no auth.
Liveness expects 200. Readiness expects 200 only for healthy registered checks;
unhealthy/degraded expects 503. All responses have an empty body. The same paths
exist on each API's internal port8080 and are not published directly. An unavailable
process gives no HTTP response. These routes are implemented source only: they
have not been compiled, deployed or demonstrated reachable by this W1 pass.
The health tests cover Auth's actual adapter; other service adapters and real
database, broker, issuer/trust and upstream availability still require runtime proof.
