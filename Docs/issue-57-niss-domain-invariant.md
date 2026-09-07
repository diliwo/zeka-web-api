# Issue #57: Client NISS contract

Scope: [GitHub issue #57](https://github.com/diliwo/zeka-web-api/issues/57).
This records the implementation policy; it does not amend an ADR.

## Accepted representation and validation

- A NISS is a string of exactly eleven ASCII digits. Leading zeros are significant.
- Neither the API nor Domain trims, removes separators, converts Unicode digits, or otherwise repairs input. Formatted input is rejected. Callers must submit the canonical string.
- The first nine digits form a decimal number `B`; the last two form `C`. Accept when `C = 97 - (B mod 97)` or `C = 97 - ((2000000000 + B) mod 97)`. The second calculation implements the prefix used for births from 2000 onward. A zero remainder produces check 97, never 00.
- The same checks cover RN and BIS forms, including BIS month offsets of 20 or 40. Do not remove the offset before calculating the check.
- Do not infer birth date, sex, nationality, or century from the identifier. Unknown/incomplete birth dates are permitted by the official schemes; no calendar validation or comparison with the Client's demographic fields is performed.
- Passing these checks is evidence of canonical syntax and checksum only. It is not proof of assignment, identity, or authorization, nor validation of every registry allocation rule.

Sources: [BCSS register description and BIS cautions](https://bcss.fgov.be/fr/project/registre-national-registres-bcss), [National Register TI000 instructions](https://www.ibz.rrn.fgov.be/sites/default/files/documents/fr/registre-national/instructions/liste-TI/TI000_Numero-identification.pdf), and [Dimona canonical entry instructions](https://www.socialsecurity.be/site/v2/dimona/fr/dimona/scenario/fields/action_insz.html). No example identifier from those sources is used in code or tests.

## Enforcement and architectural boundaries

Accepted Ubongo ADRs consulted: ADR-001 (Overall Microservice Architecture), ADR-002 (Clean Architecture for Microservices), and ADR-003 (Multi-tenancy and Data Isolation Strategy).

`Core.ValueObjects.Niss` owns the invariant without persistence or delivery dependencies. Client creation and every assignment to `Client.Ssn` parse the value first; failed reassignment leaves the previous value intact. The empty constructor is private for EF materialization. The existing public `Ssn` string and PostgreSQL text column are retained, and EF explicitly uses the guarded property. Invalid legacy rows cannot materialize as valid Domain entities.

`AddClientCommand` uses the guarded constructor. The native-language and Ibis patch handlers validate canonical NISS before lookup; the patch DTO cannot change NISS. `UpdateClientCommand` and the infrastructure `ClientService` import methods are existing no-op prototypes: this issue does not implement those workflows. Future imports must construct/update through the Domain API; direct SQL and bulk updates are not supported invariant-preserving import paths.

Client remains organisation-owned. Organisation identity, tenant keys, authorization requirements and service data ownership are unchanged. NISS is not an authorization credential. No uniqueness rule or database constraint is introduced by this issue.

## HTTP contracts and consumers

| Operation | Service route | Gateway route | JSON body |
| --- | --- | --- | --- |
| Change native language | `PATCH /api/clients/native-language` | `PATCH /clients/native-language` | `niss`: canonical string; `language`: existing language name |
| Search | `POST /api/clients/search` | `POST /clients/search` | `searchText`: string |

The old `GET /api/clients/searchtext/{text}` and `POST /api/clients/updatelanguage/{ssn}/{language?}` routes are removed. There is no redirect or compatibility alias that carries identifiers in URLs. Search is free text, including partial identifiers; it does not apply the complete-NISS parser. Search responses use `Cache-Control: no-store, no-cache`, including action-level validation errors.

NISS validation failures return a generic 400 response without echoing input. Duplicate-client exceptions contain no identifier and map to 409. Body binding failures return generic 400 responses. The generated OpenAPI describes the new body contracts; HTTP contract tests exercise the real controller, MVC/Newtonsoft binding, exception filter, authorization requirement, cache filter and Swagger generation with substitute Application handlers. They do not assert real JWT validation or tenant authorization conformance.

The in-repository Ocelot configuration and `.http` example are updated. No frontend consumer source is present in this repository. Coordinate separately deployed consumers before rollout; this branch cannot update external repositories or deployed clients without authorization. Keep the existing language-name invariant; for example use `Français`, not a two-letter language code.

## Sensitive data and diagnostics

- No NISS or search request body is passed to the request, performance, or exception logging behaviours. Exception logging records type and request name, not exception messages or exception objects that may hold personal data.
- The native-language console output is removed. NISS-specific exceptions do not accept/store identifiers. The NISS value object's diagnostic string is redacted; its `Value` and `Client.Ssn` remain intentionally sensitive and must never be destructured into logs.
- The current service does not enable HTTP body logging or EF sensitive-data logging. Do not enable either in deployment, proxies, tracing agents or support tooling. Suppress both request and response payload capture for the sensitive routes, and do not place NISS in telemetry attributes, baggage or metric labels.
- Tests generate synthetic data; test cases are named by scenario, and assertions avoid printing identifier values. Runtime-generated synthetic data is not proof that a number is unassigned. Never copy real identifiers into fixtures, Swagger examples, traces, or bug reports.

Before traffic is switched, configure ingress/gateway/APM redaction for legacy URLs, including rejected requests. Removing an application route does not stop a proxy from logging the original URL before routing. Verify redaction using synthetic values at every deployed hop, including failed/unauthenticated requests. Ingress redaction, deployed collector settings and external consumer updates require operational verification; local contract tests do not prove them.

## Existing data: required rollout and future constraint plan

No migration rewrites identifiers, deletes clients, or adds a database check in this issue. Do not deploy the stricter materialization guard over unreviewed prototype data.

1. Back up the affected service database using the approved restricted procedure. Inventory all Client rows, including soft-deleted rows, within authorised organisation scopes. Use a restricted audit process to run the same canonical/checksum validation. Report only counts, internal Client IDs and Organisation IDs; never output raw NISS, failing payloads, query parameters or exception messages.
2. Classify invalid rows as formatting problems, missing/placeholder values, or checksum/identity discrepancies. Do not guess missing digits, recompute a check digit to manufacture an identity, or fill production rows with synthetic test values.
3. An authorised data owner must verify corrections against the approved source of truth. Review a proposed correction set and its organisation ownership before applying it transactionally through an authorised remediation process. Retain restricted audit evidence without copying identifiers into ordinary logs. Preserve relationships and tenant keys.
4. Unresolved rows block rollout. Arrange an explicitly approved quarantine/remediation plan if correction is impossible; this issue does not authorize deletion or reassignment. Re-audit until no invalid rows remain, pause legacy writers, then deploy the Domain guard and coordinated consumers.
5. Only after cleanup and an authorised follow-up migration should PostgreSQL enforce the same length, ASCII and dual-checksum rule for direct writers. Evaluate `CHECK ... NOT VALID` followed by `VALIDATE CONSTRAINT` with the production provider, lock/load limits, null behavior, integer width and the existing tenant controls. Use PostgreSQL tests against the reviewed legacy-data scenarios before claiming conformance.
6. Rollback must preserve remediated values, tenant relationships and privacy controls. Do not restore the removed sensitive URL routes to regain compatibility; coordinate consumers instead. Record backup/recovery ownership and a deployment decision before changing live data.

## Verification limits and discovered issues

Current verification: the full solution builds. The full regression run has 220 passed, zero failed and zero skipped tests. Client Domain has 32 passing tests; six Application tests and nine HTTP/OpenAPI/gateway contract cases pass. PostgreSQL tests verify canonical NISS round trips, guarded updates and rejection of invalid legacy rows. The original failing NISS Domain test is fixed without being removed or skipped.

The PostgreSQL tests use the actual service context and PostgreSQL 17. They exposed a pre-existing defect: `ApplicationDbContext` wrote local audit timestamps into `timestamp with time zone` columns, which Npgsql rejects. With explicit user approval, all four creation/modification audit assignments now use `DateTime.UtcNow`. Tests exercise both synchronous and asynchronous saves and verify UTC timestamps before saving and after PostgreSQL materialization. No schema change or historical-data rewrite is needed for this repair.

Package restore also reports pre-existing duplicate references and an AutoMapper 13.0.1 vulnerability warning. Dependency remediation is outside #57. No build or test result establishes production readiness or substitutes for the data audit and deployed privacy verification above.
