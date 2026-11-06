# W3 Auth dependency trace

## Scope and provenance

This is a source-bound dependency trace for the approved W3 database-bootstrap checkpoint. It does not implement or authorize TLS, administrator provisioning, worker creation, synthetic seeding or broker-package changes.

- Worktree: D:\Workspace\zeka-m1-w3-synthetic-seeding.
- Inspected W3 source: ca26e36c6a5f4e473b59c4f7e8072e3de826400c.
- Inherited W1: 55f75843077f65f445e7da201e3feab001490c3a.
- Inherited W2: e5de89962ca47aeba973edab22bc78e1faa45f4b.
- Execution: owner-local Windows, normal inherited environment. The checkpoint executor records SDK 8.0.129; source reading used PowerShell 7.4.22 on .NET 8.0.33. Container .NET is 8.0.29.
- Docker recorded by the checkpoint executor: Rancher Desktop WSL, Docker Engine 29.5.3, default context, engine ID 0b62f5ec-15d9-4e4b-a450-817d52378eef.
- Source reads were preceded by successful deterministic Sonar secrets scans. No private configuration, credential values or signing keys were read for this trace.
- The trace itself ran no application, database or migration tests and created no users, sessions, workers, mappings, certificates or hosts entries. Runtime observations below came from the separate read-only delivery trace.
- Later bootstrap-only changes do not replace this inspected Auth/shared-source revision. Recheck citations if those source files change.

Approval: [ZEK-17 W3 checkpoint](https://linear.app/zeka-saas/issue/ZEK-17#comment-db579109-02e2-4424-8cc4-f515d51242c9).

All local source links below refer to the worktree above, at the inspected revision. Line ranges in labels describe the inspected source; each link opens its first relevant line.

## Identity and permission dependencies

| Identity or operation | Existing path | Required boundary / missing capability |
| --- | --- | --- |
| First administrator | Registration delegates to UserManager.CreateAsync. User.Create starts PendingVerification. Organisation creation/activation and owner-membership factories exist. | No complete production entrypoint composing user activation/email confirmation, active organisation and owner membership was found in the inspected Auth production sources. Reuse those mechanisms through a scoped provisioner. A database bootstrap or permission-set migration does not create a usable administrator. |
| Staff created by the seed operator | AdminArea CreateStaffMember requires ManageStaffProfiles, verifies an active Auth membership, persists staff and stages a projection. | An AdminArea staff record is not an Auth login, an organisation membership, or a worker identity. Provision/enrol Auth identities before linking staff to them. |
| AdminArea retry worker | TenantWorker:SubjectId, BearerToken and OrganisationIds are configured separately. Startup rejects missing configuration. Every organisation attempt asks Auth for current ManageStaffProfiles authority before tenant database work. | A distinct enrolled subject, valid membership/permission, active session-bound credential and explicit renewal/delivery mechanism are required. The worker contains no credential issuer or refresh caller. |
| Client projection consumer | Uses its own configured TenantWorker subject/token on message delivery. Current permission and live membership checks precede projection persistence. | Requires a second distinct enrolled subject and independently managed credential. Do not copy the first administrator's or AdminArea worker's session. No worker enrolment or renewal path was found in this consumer. |
| Narrow worker authorization | ManageStaffProfiles currently belongs to the Owner/Admin permission sets. Explicit membership grants support Organisations.Export. | There is no demonstrated narrow ManageStaffProfiles grant for a lower-privilege worker role. Do not silently grant Owner/Admin to make the workflow run. The worker permission contract needs an explicit scoped decision/implementation. |

Source bindings:

- [Registration:12–29](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/IdentityAuthenticationService.cs:12); [HTTP registration:16–42](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Endpoints/AuthApiEndpoints.cs:16); [PendingVerification user creation:22–44](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/Models/User.cs:22).
- [Organisation factories and activation:30–69](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Core/Organisations/Organisation.cs:30); [active membership / owner factory:27–56](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Core/Organisations/OrganisationMembership.cs:27).
- [Staff command permission:12 and handler:37–64](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Application/Staffs/Commands/CreateStaffmember/CreateStaffMemberCommand.cs:12).
- [AdminArea worker startup:23–36](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionRetryWorker.cs:23); [per-organisation authorization / transaction / retry:61–100](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionRetryWorker.cs:61).
- [Client message authorization, membership and persistence:18–81](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/ClientManagement/Client.Infrastructure/Messaging/StaffProjectionConsumer.cs:18).
- [Permission catalogue and explicit-grant contract:14–50](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Core/Organisations/TenantPermissions.cs:14); [grant-store restriction:48–53](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Persistence/Services/MembershipPermissionGrantStore.cs:48).

The negative onboarding finding is bounded: a scanned search of non-test, non-migration Auth C# production sources found the domain constructors/mutators and eligibility checks, but no caller composing first-admin activation/confirmation/enrolment. It is not a claim that an uninspected external provisioner cannot exist.

## Credential lifecycle

Auth's tenant-access endpoints require both a valid bearer token and exactly one nonempty session sid tied to an active stored browser-session family. The subject-only AccessTokenIssuer.IssueAsync method is insufficient for these worker calls. IssueSessionAsync can sign a session token, but does not itself establish a valid stored session or enrol a subject.

- Sign-in checks password and user eligibility, records a session family and refresh credential, then issues the access token.
- Eligible means Active user status, confirmed email and no current lockout.
- Session validity includes idle/absolute deadlines, revocation and matching security stamp. Losing eligibility or changing the stamp invalidates the relevant family.
- Restore rotates the refresh credential, caps the sliding idle deadline at the absolute deadline and issues another access token. Refresh-credential reuse revokes the family.
- Access-token issuance is bounded by configured lifetime and a maximum of one hour. Session policy requires idle lifetime at least as long as access lifetime and absolute lifetime at least as long as idle lifetime.
- Actual configured lifetimes and a populated worker credential are unverified; private configuration was deliberately not inspected.
- No automatic worker refresh/reissuance/delivery loop was found in either worker path. Browser restore also has explicit Origin, CSRF and trusted-proxy admission requirements; it cannot be replaced by fabricating a sid or copying an interactive cookie.

Sources: [tenant-access session gate:65–81](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Endpoints/TenantAccessEndpoint.cs:65); [session sign-in/restore:29–114](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/BrowserSessions.cs:29); [active checks/revocation:133–160](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/BrowserSessions.cs:133); [eligibility/deadline:250–254](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/BrowserSessions.cs:250); [session policy:8–15](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Identity/BrowserSessionPolicy.cs:8); [issuer methods:64–106](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs:64); [maximum access lifetime:16–20](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Authentication/IssuerOptions.cs:16).

## Startup, readiness, request and message triggers

| Trigger | Actual dependency |
| --- | --- |
| Service composition/startup | Registers database identity guards, authentication options and hosted workers. AdminArea retry-worker startup requires its own configuration. It does not create Auth users or credentials. |
| Auth JWKS/readiness request | JWKS preparation loads signing material; successful JWKS response completion confirms publication. Issuer readiness checks the currently confirmed key. |
| Consumer API readiness | Shared JWKS readiness probes resolve the configured JWKS endpoint. A configured URL or an open port is not proof of successful trust/discovery. |
| Protected HTTP request | JWT issuer/audience/signature are validated. Auth tenant-access then checks the live session and current membership/permission. |
| AdminArea retry cycle | Calls live Auth authority before dispatching pending tenant work; failures retain retry work. |
| Client message | Calls Auth for current worker permission and membership before applying the projection, including before duplicate/revision handling. The external package's acknowledgment behavior remains consequential. |

Sources: [Auth composition/endpoints:11–35](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Program.cs:11); [Auth local health:54–94](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Program.cs:54); [issuer publication/readiness endpoints:9–28](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Endpoints/IssuerEndpoints.cs:9); [shared bearer/JWKS readiness:64–75,118–122](D:/Workspace/zeka-m1-w3-synthetic-seeding/Shared/Zeka.Authentication/BearerAuthentication.cs:64); [JWKS shared deadline and gate:51–80](D:/Workspace/zeka-m1-w3-synthetic-seeding/Shared/Zeka.Authentication/JwksTrust.cs:51).

AdminArea's current-authority client uses one two-second deadline across up to two safe GET attempts and the short backoff. TLS/transport errors and deadline exhaustion become Unavailable; 401 becomes Unauthenticated and 403 Denied. Client's membership client also has a two-second request budget; 204 is membership success, 401 throws unauthenticated, 403 returns false and other failures are unavailable. Sources: [current authority:34–100](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Authorization/CurrentTenantAccessClient.cs:34); [membership:12–29](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/ClientManagement/Client.Infrastructure/Authorization/StaffMembershipClient.cs:12).

## Canonical issuer, TLS and name resolution

The approved canonical issuer is **https://auth.zeka-m1.test:19443**. Issuer identity comes from AuthenticationOptions.Issuer and is copied into the JWT descriptor; it is not derived from the request Host. HTTPS issuer and JWKS addresses are required by validation.

Future wiring must make the same hostname and port reachable from the intended service containers and Windows, with an actual TLS listener on port 19443, matching certificate SAN, validated trust, an isolated-network alias and the approved loopback-only host publication. Any owner-required hosts change remains a separate prerequisite. The browser Origin is a distinct, explicitly configured admission value; do not infer it from the issuer or Host, and do not bypass certificate or proxy validation.

Current checked-in Compose configures Auth HTTP port 8080 and mounts a public CA bundle, signing key and session hash key. It has no 19443 listener/publication, auth.zeka-m1.test network alias or server-certificate delivery. The gateway's published address is 127.0.0.1:19000:8080. A CA bundle mount does not prove serving TLS or successful HttpClient trust.

At approximately **2026-10-29 22:55 UTC**, the separate read-only delivery trace observed zero Windows listeners on 19443, zero Rancher-WSL listener matches, zero Docker published-port matches, and no A record returned for auth.zeka-m1.test. These are point-in-time observations, not a port reservation or proof that every interface, namespace or resolver is clear. No host/DNS configuration was changed. Recheck immediately before a future authorized TLS binding.

Sources: [configured JWT issuer:89](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.Infrastructure/Authentication/AccessTokenIssuer.cs:89); [HTTPS option validation:32–37](D:/Workspace/zeka-m1-w3-synthetic-seeding/Shared/Zeka.Authentication/AuthenticationOptions.cs:32); [current Auth Compose:61–84](D:/Workspace/zeka-m1-w3-synthetic-seeding/Deployments/local-stack/compose.yaml:61); [gateway publication:151–161](D:/Workspace/zeka-m1-w3-synthetic-seeding/Deployments/local-stack/compose.yaml:151); [browser Origin/proxy/CSRF admission:93–104,149–155](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AuthManager/AuthManager.API/Endpoints/BrowserSessionEndpoints.cs:93).

## EventBus delivery dependency

The delivery trace verified package **EventBus 1.2.0** against its nuspec-pinned source commit **75ab9f4d6c57ee37dd615ce4f32ef44f87698737**, repository diliwo/eventbus-dotnet-lib, using RabbitMQ.Client 6.8.1.

| Package behavior | Source evidence | Consequence |
| --- | --- | --- |
| Synchronous BasicPublish followed by Task.CompletedTask; mandatory false; basicProperties null; no publisher confirms in this path | [RabbitMqEventBus.cs:18–40](https://github.com/diliwo/eventbus-dotnet-lib/blob/75ab9f4d6c57ee37dd615ce4f32ef44f87698737/Eventbus/RabbitMq/RabbitMqEventBus.cs#L18-L40) | Awaiting PublishAsync is not evidence of broker-confirmed durable delivery. |
| Non-durable queue/exchange declarations and autoAck true | [RabbitMqHostedService.cs:28–74](https://github.com/diliwo/eventbus-dotnet-lib/blob/75ab9f4d6c57ee37dd615ce4f32ef44f87698737/Eventbus/RabbitMq/RabbitMqHostedService.cs#L28-L74) | Broker restart durability and failure-driven redelivery are not established. |
| Handler.Handle returns Task, but the invocation is not awaited | [RabbitMqHostedService.cs:77–94](https://github.com/diliwo/eventbus-dotnet-lib/blob/75ab9f4d6c57ee37dd615ce4f32ef44f87698737/Eventbus/RabbitMq/RabbitMqHostedService.cs#L77-L94), [IEventHandler.cs:4](https://github.com/diliwo/eventbus-dotnet-lib/blob/75ab9f4d6c57ee37dd615ce4f32ef44f87698737/Eventbus/Abstractions/IEventHandler.cs#L4) | Asynchronous Client Auth/TLS/session failures can occur after acknowledgment without explicit nack/requeue/DLQ recovery in this code. |

AdminArea marks the outbox row PublishedAt after the publisher task completes. Its retained pending retry therefore does not establish recovery once this package has accepted publication but Client subsequently fails. Additionally, CreateStaffMember dispatches before returning, while the authorization behavior wraps the handler in a transaction whose commit follows the work callback. This statically establishes publication-before-outer-commit exposure; no live failure was induced.

Sources: [outbox publish/mark/retry:58–90](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Messaging/StaffProjectionOutbox.cs:58); [handler dispatch:64–66](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Application/Staffs/Commands/CreateStaffmember/CreateStaffMemberCommand.cs:64); [authorization transaction wrapper:16–27](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Application/Common/Behaviours/AuthorizationBehaviour.cs:16); [work before commit:217–218](D:/Workspace/zeka-m1-w3-synthetic-seeding/Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/Persistence/TenantTransactionAttempt.cs:217).

These findings identify a delivery dependency; they do not authorize a package correction, package release or production change in this checkpoint.

## Explicit future failure/recovery test

Under separate approved implementation/test scope:

1. Create the eligible first administrator and two distinct workers through the approved enrolment/credential path. Record each subject, organisation, permission and session lifetime without publishing credential values.
2. Verify one committed staff change projects through AdminArea, RabbitMQ and the real Client consumer within a bounded deadline. Do not write Client projection rows directly.
3. Coordinate delivery with an expired/revoked worker session, denied permission, unavailable Auth, or failed TLS validation. Assert that Client persists no unauthorized projection and capture the actual acknowledgment plus outbox state. The current auto-ACK/unawaited callback is expected to expose a gap; do not label message retention/recovery PASS without observing it.
4. Restore authority through the approved credential renewal/delivery mechanism. Verify the failed message can recover to exactly one expected projection within a bounded timeout. Verify duplicate delivery/revision handling leaves the expected persisted state unchanged.
5. Exercise the approved service/broker restart cases. Verify committed work is not lost and normal restart does not republish synthetic seeds. Queue durability, publisher confirmation and callback completion require actual evidence.
6. Keep every outcome, stop at the first mandatory failed gate, and report exact revisions and failure stage. No skips, timeout increases, repeated-until-green retries or authentication bypasses.

## Effort and milestone risk

These are rough engineering estimates from source inspection, not commitments or demonstrated readiness. They assume one focused implementer, existing contracts, prompt scoped decisions, normal local infrastructure, meaningful security/negative tests and ordinary review availability.

| Future capability | Rough effort | Main assumption |
| --- | --- | --- |
| First-admin provisioner with activation/confirmation, organisation/owner enrolment, refusal and repeatability tests | 1–2 engineering days | Existing domain/Identity mechanisms are reused; no broad onboarding redesign. |
| Two worker identities, approved narrow permissions, issuance/renewal/revocation and secure credential delivery | 1–3 engineering days | A scoped worker permission/session contract is accepted promptly. EventBus correction/release is excluded. |
| Canonical HTTPS19443 listener/routing, certificate/trust separation and browser admission verification | 1–2 engineering days | Local naming/hosts prerequisites can be completed and existing hosting/configuration mechanisms suffice. |
| Integrated projection, failure/recovery evidence and review | Another 0.5–1 engineering day | Delivery and identity prerequisites are resolved. |

The **4 November 2026 first-stack target is at risk** while administrator onboarding, distinct worker credentials, TLS and delivery recovery remain unresolved. **6 November 2026 C1 is conditional** on prompt scoped decisions and passing final-candidate integration/failure evidence. Static inspection alone does not establish that either date is impossible, nor justify reporting the stack ready. A package correction/release or a broader authorization decision would require separately authorized effort.
