# MVP pull-request validation

The `MVP Validation` workflow validates pull requests targeting `develop`. It
is validation automation only: it builds and tests repository code and images,
but never deploys or promotes an environment.

## Check structure

The workflow exposes four component checks and one aggregate check:

| Check | Scope |
| --- | --- |
| `Build and non-PostgreSQL tests` | Credential guard, solution restore/build, and the seven test projects listed below |
| `PostgreSQL Testcontainers` | The three existing PostgreSQL/Testcontainers integration projects |
| `Docker images (4)` | Compose builds for AdminAreaManagement API, ClientManagement API, AuthManager API, and Ocelot API Gateway |
| `SonarQube Cloud / Quality Gate` | Pull-request analysis and a blocking Quality Gate wait |
| `MVP Validation` | Always runs and succeeds only when all four required component jobs succeed |

Superseded runs for the same pull request are cancelled. Component jobs retain
their own logs so the aggregate outcome does not hide the failing subsystem.

## Exact test scope

`Build and non-PostgreSQL tests` executes:

- `Services/AuthManager/Tests/Domain.UnitTests/Domain.UnitTests.csproj`
- `Services/AuthManager/Tests/Application.UnitTests/Application.UnitTests.csproj`
- `Services/AuthManager/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj`
- `Services/AdminAreaManagement/Tests/Domain.UnitTests/Domain.UnitTests.csproj`
- `Services/AdminAreaManagement/Tests/Application.UnitTests/Application.UnitTests.csproj`
- `Services/ClientManagement/Tests/Domain.UnitTests/Domain.UnitTests.csproj`
- `Services/ClientManagement/Tests/Application.UnitTests/Application.UnitTests.csproj`

`PostgreSQL Testcontainers` executes these projects against their existing
`postgres:17-alpine` Testcontainers dependencies:

- `Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj`
- `Services/AdminAreaManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj`
- `Services/ClientManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj`

The workflow does not add filters, exclusions, skips, warning suppression, or
test-result rewriting. Every project in each list is attempted so one known
failure does not prevent evidence from the remaining projects.

## Current baseline evidence

At the Issue #51 implementation base
`a1d3e6c44b2405074b9e29ca113f4c764c2ae7a1`, a clean authenticated restore
and Release build succeed, but the non-PostgreSQL test component faithfully
reports 11 pre-existing application/domain failures:

- six failures in AdminAreaManagement `Domain.UnitTests`;
- four failures in AdminAreaManagement `Application.UnitTests`; and
- one failure in ClientManagement `Domain.UnitTests`.

ClientManagement `Application.UnitTests` currently discovers no tests. The
AuthManager suites in that component pass. All three PostgreSQL/Testcontainers
projects pass (AuthManager 26/26, AdminAreaManagement 4/4, and ClientManagement
3/3), and all four development images build. These baseline failures and
warnings remain application-owned and are not hidden or changed by this CI work.

## Permissions, package authentication, and forks

Workflow permissions default to none. Component jobs receive only
`contents: read` and `packages: read`; the source-policy and aggregate jobs
receive no token permissions.

For same-repository pull requests, restore uses NuGet's supported
`NuGetPackageSourceCredentials_github` process environment variable. It prefers
the approved repository secret `ZEKA_PACKAGES_READ_TOKEN` and otherwise tries
the job's short-lived `GITHUB_TOKEN`. The fallback works only when the private
packages grant this repository read access. The credential exists only in the
restore step environment. Docker Compose maps that credential to the existing
BuildKit secret mount, so it is not a build argument, copied file, layer, image
label, or logged value.

Fork pull requests are classified before any checkout. Secret-dependent jobs
are skipped for forks, and the aggregate check fails with instructions to
validate the exact commit from a same-repository branch. The workflow uses
`pull_request`, never `pull_request_target`, so untrusted fork code is not run
with package or Sonar credentials.

## Reproducibility policy

`global.json` requires .NET SDK `8.0.129` with roll-forward disabled. The CI
setup step installs that exact version. Microsoft Container Registry does not
publish the Ubuntu-distribution SDK number as a container tag, so the four
Dockerfiles independently pin the published SDK `8.0.419` and ASP.NET Core
runtime `8.0.29`. The Docker context excludes the host-only `global.json` so
the container build uses its explicitly pinned SDK. SonarScanner for .NET is
pinned in the local tool manifest at `11.3.0`.

NuGet lock files are deliberately not introduced by this MVP. Direct package
versions remain fixed in project files, but transitive dependency resolution
can change when upstream package metadata changes. The workflow therefore
guarantees the declared SDK/tool versions and a clean restore from the tracked
source configuration; it does **not** claim byte-for-byte deterministic NuGet
resolution. Introducing and maintaining solution-wide lock files should be a
separate dependency-management change with its own baseline review.

Third-party Actions are pinned to immutable commits:

| Action release | Commit |
| --- | --- |
| `actions/checkout` 4.2.2 | `11bd71901bbe5b1630ceea73d27597364c9af683` |
| `actions/setup-dotnet` 4.3.1 | `67a3573c9a986a3f9c594539f4ab511d57bb3ce9` |

## Required external configuration

An authorized repository/Sonar administrator must provide or confirm:

1. The private GitHub Packages grant allows this repository's `GITHUB_TOKEN`
   to read the required `Zeka.Extensions.*` packages, **or** repository secret
   `ZEKA_PACKAGES_READ_TOKEN` contains the approved least-privilege package-read
   credential from Issue #23.
2. A SonarQube Cloud project exists and is bound to this repository with CI
   analysis enabled. Sonar automatic analysis must not compete with CI
   analysis.
3. Repository variable `SONAR_ORGANIZATION` contains the Sonar organization
   key and `SONAR_PROJECT_KEY` contains the project key.
4. Repository secret `SONAR_TOKEN` contains an analysis token scoped only as
   required for that project.
5. The project's Quality Gate emphasizes new-code conditions and is available
   to pull-request analysis.

The workflow fails clearly when a required Sonar variable or secret is absent.
Creating projects, changing package access, configuring repository secrets or
variables, changing the Quality Gate, and selecting required branch-protection
checks are external administrator actions and are not performed by this
workflow.

## Local equivalents

Use the approved 1Password-backed restore path documented in
`Docs/security/private-package-restore.md`, then run:

```bash
dotnet build zeka-web-api.sln --configuration Release --no-restore
dotnet test zeka-web-api.sln --configuration Release --no-build --no-restore
```

Before daemon-dependent local verification, confirm `docker info` reports a
rootless daemon. Build the repository's four Compose images through:

```bash
./Deployments/build/local/dev.sh build
```

The local wrapper is not used on GitHub-hosted runners because CI receives
ephemeral GitHub credentials directly and does not depend on a developer's
1Password session or local rootless-Docker socket.
