# PR #63: Docker restore and Sonar delivery correction

Reviewed starting SHA: `4e64fe0c57d9dc4ce56e8752e59e08f8115c2aa8` (Issue #44).
Verification date: 2026-09-12. This report belongs to the correction commit containing it.

## Scope and diagnosis

- Reproduced the AuthManager Docker failure locally with the tracked Dockerfile: restore skipped `Shared/Zeka.Authentication/Zeka.Authentication.csproj`, then build failed with NETSDK1004 for its missing `obj/project.assets.json`. CI run 34693751626, job 103564175198 reports the same failure.
- The complete project-reference graph consists of AuthManager API, Application, Core, Infrastructure, and Shared/Zeka.Authentication. Adding the shared project COPY before restore makes all five available. The base/build/publish/final stages, no-restore build, no-build publish, image versions, and runtime behavior remain unchanged.
- Sonar PR 63 issue `AaCV6QSq6BeFNClEDfc1`, rule `csharpsquid:S3453`, flags ClientManagement `TrainingField.cs:5`: its only constructor was implicitly private. The constructor is now public; validation is unchanged. Three tests exercise valid, null, and empty names.
- Sonar reported 403 uncovered new executable lines across 26 production files. The analysis job generated/imported no coverage. Six test projects also lacked the existing coverlet collector. They now use the same private `coverlet.collector` 6.0.0 dependency as the other six projects. The Sonar job executes the complete suite, including Docker-backed integration tests, and imports OpenCover reports before completing analysis. No gate thresholds, exclusions, job identities, permissions, or credentials changed.
- Eight additional focused tests cover previously unvisited HTTP JWKS reader and issuer configuration branches: declared/chunked response limits at 65,536 and 65,537 bytes, HTTP 503 rejection, and unexpected/nested issuer configuration. The transport tests use a real private loopback HTTP server; production HTTPS validation is unchanged.

## Verification

Environment: Windows, .NET SDK 8.0.129; active Docker endpoint `npipe://./pipe/docker_engine` for Testcontainers, Rancher Desktop Linux containers.

```text
dotnet test Services/ClientManagement/Tests/Domain.UnitTests/Domain.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~TrainingFieldTests
dotnet test Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~JwksHttpSourceTests|FullyQualifiedName~Unknown_or_nested_issuer_configuration"
dotnet build zeka-web-api.sln -c Release --no-restore
dotnet test zeka-web-api.sln -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --results-directory TestResults/pr63-final -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
docker build --progress plain -f Services/AuthManager/AuthManager.API/Dockerfile -t zeka-authmanager-pr63 .
```

- Focused tests: 3/3 and 8/8 passed.
- Final complete Release suite: **506 passed, zero failed, zero skipped**, across all 12 test projects. AdminArea: 60/42/4/116; AuthManager: 23/3/1/83; ClientManagement: 35/16/15/108 (Domain/Application unit/Application integration/Infrastructure respectively).
- Release build succeeded. Warning totals depend on incremental compilation; they are not used as cross-build provenance. The existing AutoMapper advisories remain outside this correction.
- Corrected Docker image built through final stage: `sha256:9a517db50b3f38853c39d0856a2d011d3f4d5ba21c63a783460443b133f8187c`.
- Twelve OpenCover reports generated. Matching their sequence points to the exact Sonar PR 63 new-line inventory yields **372/403 lines covered**. Merging branch visits by file/line/offset/path gives **263/334 branches**, a local combined estimate of **86.16%**. This is a local comparison against the reviewed SHA's new-line inventory, not a server Quality Gate result. The newly public TrainingField constructor is also exercised.
- `actionlint` 1.7.12 passed for the workflow; `git diff --check` passed.
- Sonar deterministic delta secret scan and isolated delta Gitleaks/key-artifact scans passed. This does not claim the complete repository is scanner-clean. The previously recorded develop Kubernetes Secret finding remains outside Issue #44 scope; its value was not inspected or reproduced.
- The available `sonar analyze --file ... -p diliwo_zeka-web-api` command performed its secret check but could not execute code analysis: Vortex is unavailable on this connection. The exact server S3453 rule was inspected and its recommended constructor accessibility repair applied. Remote Quality Gate confirmation requires CI reanalysis of this commit.

## Exact production new-line inventory

The following line numbers come from Sonar `/api/sources/lines` for PR 63 at the reviewed starting SHA; all were reported uncovered before coverage import. Local coverage above unions all twelve final reports. Child-process host execution is not assumed to be covered by the in-process collector.

| Production file | Sonar new executable lines |
| --- | --- |
| `Services/AuthManager/AuthManager.Infrastructure/Migrations/20260910165430_TenantPermissionCatalogue.cs` | 16, 39, 45, 49, 50, 52, 56, 57, 59, 61, 63, 64, 65, 66, 67, 68, 70, 84 |
| `Services/AuthManager/AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs` | 28, 30, 36, 37, 38, 39, 41, 42, 44, 46, 50, 51, 57, 58, 59, 61, 65, 69, 70, 71, 72, 73, 76, 78, 81, 84, 86, 87, 88, 90, 91, 92, 95, 98, 99, 100 |
| `Services/AuthManager/AuthManager.Infrastructure/Identity/ActiveOrganisationMembership.cs` | 12, 13 |
| `Services/ClientManagement/Client.Core/Entities/Assessment.cs` | 87, 88 |
| `Shared/Zeka.Authentication/AuthenticationOptions.cs` | 10, 13, 19, 20, 21, 22, 23, 24, 25, 26, 28, 32, 33, 34, 35, 36, 37, 39, 40, 41 |
| `Shared/Zeka.Authentication/BearerAuthentication.cs` | 18, 23, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 51, 53, 56, 57, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 79, 80, 81, 82, 83, 84, 86, 87, 90, 91, 94, 96, 98, 99, 100, 101, 102, 104, 106, 108, 109, 113, 114, 121, 122 |
| `Services/ClientManagement/Client.Core/Entities/ClientAddress.cs` | 10, 13, 14, 15, 20, 23, 24, 25, 30, 33, 34, 35, 40, 43, 44, 45, 50, 53, 54, 55 |
| `Services/ClientManagement/Client.Core/Entities/CurrentAssignment.cs` | 9, 10 |
| `Services/AuthManager/AuthManager.Application/Authorization/CurrentTenantAccess.cs` | 14 |
| `Services/AuthManager/AuthManager.Infrastructure/Identity/CurrentTenantAccessResolver.cs` | 14, 15, 16, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 37, 38, 41, 42, 43 |
| `Services/AuthManager/AuthManager.Infrastructure/DependencyInjection.cs` | 39, 40 |
| `Services/AuthManager/AuthManager.API/Endpoints/IssuerEndpoints.cs` | 9, 11, 12, 13, 16, 17, 18, 20, 22, 24, 25, 26, 27 |
| `Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerOptions.cs` | 17, 26, 27, 28, 29, 30, 33, 34, 36, 37, 39, 43, 44, 45, 46, 47, 48, 49, 50, 51 |
| `Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerRegistration.cs` | 11, 12, 13, 14, 15, 16, 17, 18, 19 |
| `Shared/Zeka.Authentication/JwksTrust.cs` | 15, 16, 17, 18, 22, 24, 25, 27, 39, 46, 47, 48, 53, 54, 57, 60, 61, 62, 63, 65, 66, 68, 71, 72, 73, 75, 76, 78, 79, 80, 81, 82, 84, 86, 87, 92, 93, 94, 95, 96, 98, 101, 102, 103, 106, 107, 109, 110, 111, 112, 113, 114, 115, 116, 118 |
| `Services/AuthManager/AuthManager.Infrastructure/Persistence/MigrationContextFactory.cs` | 10, 11 |
| `Services/AdminAreaManagement/AdminAreaManagement.Core/Entities/PartnerAddress.cs` | 10, 13, 14, 15, 20, 23, 24, 25, 30, 33, 34, 35, 40, 43, 44, 45 |
| `Services/AuthManager/AuthManager.Core/Organisations/PermissionSet.cs` | 8, 9, 10, 11, 12, 28, 29, 30, 31, 32, 33, 34 |
| `Services/AuthManager/AuthManager.API/Program.cs` | 16, 17, 19, 20, 21, 22 |
| `Services/AuthManager/AuthManager.Infrastructure/Authentication/SigningKeyProvider.cs` | 12, 13, 20, 21, 22, 23, 24, 28, 30, 34, 35 |
| `Services/ClientManagement/Client.Core/Entities/SocialCase.cs` | 16 |
| `Services/ClientManagement/Client.Core/Entities/SocialWorker.cs` | 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28 |
| `Services/AdminAreaManagement/AdminAreaManagement.Core/Entities/StaffMember.cs` | 12, 13, 14 |
| `Services/AuthManager/AuthManager.API/Endpoints/TenantAccessEndpoint.cs` | 10, 14, 15, 16, 17, 18, 21, 22, 23, 24, 25, 26, 27, 29, 30, 32, 34, 35, 38, 39, 40, 41, 42, 45, 46, 47, 48, 49, 50, 51, 53, 54, 56, 58, 62, 63, 64, 65 |
| `Services/AuthManager/AuthManager.API/TenantAuthentication.cs` | 10 |
| `Services/AuthManager/AuthManager.Core/Organisations/TenantPermissions.cs` | 17, 20, 22, 23, 24, 25, 27, 29, 31, 33, 36, 39 |

Architecture: the accepted authentication/tenant boundary and Clean Architecture constraints previously reviewed for Issue #44 remain unchanged. This correction does not alter production authentication, authorization, EF/migrations, tenant isolation, identity, RLS, or secrets. No merge, issue closure, repository settings change, or ADR-005 work was performed.
