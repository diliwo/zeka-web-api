# Zeka — Agent Engineering Contract

> **Owner-approved product-first model (21 November 2026).** Implement [Agentic Engineering Governance](https://github.com/diliwo/ubongo/blob/main/projects/Triptych/zeka%20saas/Docs/AI%20engineering/Agents/Governance/Agentic%20Engineering%20Governance.md). Accepted Ubongo ADRs remain authoritative. This repository instruction does not change installed capabilities, secrets, GitHub rulesets or CI enforcement.

## Mission, authority and autonomy

Deliver one **user-visible, independently testable Zeka vertical slice** per owner-authorized Linear objective (for example [ZEK-27](https://linear.app/zeka-saas/issue/ZEK-27)). Authorization covers the *whole slice*: related code, unit/integration/provider/browser tests, safe local restarts, routine non-destructive schema changes under approved roles, necessary dependency/lockfile changes, ordinary CI test/discovery updates, documentation, commits/pushes to task branches and a linked PR. No separate GitHub Issue, per-file permission, per-step approval, repeated local “go”, SHA ledger or arbitrary 10/30/45-minute budget is required by this instruction.

Windows **Codex** implements and tests; **Chief** reviews the finished PR once and coordinates dependencies; **Hephaestus** handles separately authorized platform or production work with actual tools and credentials. Current source is implementation evidence, not authority over accepted ADRs. If a real architecture/security conflict arises, identify the exact decision instead of guessing or silently bypassing it. Work on independent slices can run in isolated worktrees, even when an unrelated stabilization track is red.

Within the approved outcome, favor the smallest secure maintainable implementation. Inspect the relevant source, tests and accepted ADRs; use standard engineering approaches and existing test fixtures. Diagnose and correct routine failures without asking for reauthorization on every iteration. Track unrelated discovered defects separately; never silently expand the product outcome.

## Architecture and security boundaries

## Architecture baseline

Do not use this section as a substitute for reading the accepted ADRs.

At minimum, preserve the following established principles while working
on Zeka.

### Service boundaries

Zeka uses domain-aligned microservices.

A service:

- owns its business rules;
- owns its internal model;
- owns its persistence boundary;
- exposes explicit contracts;
- must not directly access another service's database.

Consequential service-boundary changes require architectural review.

### Clean Architecture

Dependencies point inward.

The standard direction is:

    Domain
        ↑
    Application
        ↑
    Infrastructure / API composition

Domain must remain independent of delivery mechanisms, persistence
technology, dependency-injection frameworks, and external systems.

Application coordinates use cases but does not own business invariants.

Infrastructure implements technology-specific adapters.

API owns HTTP delivery and composition, not business rules.

Do not introduce abstractions merely to satisfy a pattern.

### Contracts

Cross-service contracts must be explicit.

Do not expose internal domain entities or persistence models as
integration contracts.

Do not introduce source-level coupling between services when an explicit
versioned contract is required.

### Messaging

Where state changes and integration-event publication must succeed
together, preserve transactional reliability according to the accepted
architecture.

Assume duplicate delivery can occur.

Consumers requiring at-least-once delivery semantics must be designed
for idempotency.

Do not assume that the currently configured message broker represents a
final platform decision unless an accepted ADR says so.

## Multi-tenancy and security

Tenant isolation is a critical Zeka security boundary.

Before modifying tenant-aware functionality, read the accepted
multi-tenancy ADR in Ubongo.

Do not infer tenant authorization merely from possession or selection of
a tenant identifier.

Do not introduce new tenancy mechanisms based on mutable tenant names,
display names, slugs, hostnames, or similar identifiers when they
conflict with the accepted tenancy model.

Tenant isolation must be preserved across applicable:

- application operations;
- persistence;
- relational relationships;
- background jobs;
- integration events;
- outbox records;
- caches;
- files/blobs;
- audit records;
- logs;
- traces;
- metrics.

Changes affecting any of the following require explicit review before
being considered complete:

- tenant isolation;
- authentication;
- authorization;
- PostgreSQL RLS;
- privileged access;
- secrets;
- destructive migrations;
- security policies.

Never weaken a security control simply to make implementation or testing
easier.

Follow the accepted database migration authority, exact-target/history requirements, approved privileges and current RLS/security manifest. Do not replace an approved atomic step with unguarded generic `latest`, broad `GRANT CREATE` or a testing-only authority bypass. Non-destructive schema changes **within existing approved roles and grants** are ordinary engineering; changing privilege/security authority is an owner escalation.

For email verification, opening a link by GET (including email-scanner previews) must not consume a one-time token. Verification requires a deliberate confirmation and establishes a session only through existing authenticated-session policy. Preserve cross-user, replay, expiry, unverified sign-in, Origin/CSRF and anti-enumeration controls.

## Automated slice acceptance

Security is primarily enforced by **executed tests**, not serial human sign-offs.

- Validate the positive *end-to-end* user journey in appropriate local/provider/browser environments and CI. An Auth or Gateway HTTP 202 alone is not full registration acceptance.
- Add and run relevant security-negative tests: wrong user, wrong tenant, replayed or expired token, missing verification, unauthorized routes, CSRF/Origin denial, restricted-role/RLS denial, etc. Avoid irrelevant checks at a trust boundary that does not exist yet.
- Required skipped, undiscovered or missing results are **NOT_EXECUTED**, never PASS. Do not delete, weaken or relabel assertions just to satisfy CI; candidate-induced regressions must be resolved before C1 acceptance.
- Preserve locked restores, source/secret scans, protected CI and current GitHub required checks. The planned **Slice Acceptance Gate** is not an installed required check unless its actual workflow enforces it. Until then, Chief reviews existing focused positive/negative CI results and discloses enforcement gaps. Historic unrelated red failures may be tracked without blocking independent *local development*, but never waive new failures or mandated merge gates.
- Use progressive tests (affected unit → integration/contract → real PostgreSQL/Gateway → browser when applicable) and rerun focused tests after corrections. Don't invent another framework to duplicate the accepted harness. Regenerate **only affected NuGet lockfiles** via approved restore workflow, reviewing dependency changes rather than requiring per-lockfile owner approval.

## Git and owner-decision boundary

Codex may commit/push to its **non-protected task branch** and open/update a PR without another owner decision. Do not force-push, discard another agent's uncommitted work, rewrite shared history or bypass branch protection.

For M1, target `codex/m1-c1-integration` (C1) where appropriate. Chief may merge after a *single* completed-PR review when positive and security-negative acceptance, completeness, candidate-regression checks and applicable enforced GitHub requirements are satisfied. **Strict Restore alone is not proof of all slice requirements.** A merge into `develop` requires the owner's explicit approval and full MVP Validation.

**Ask the owner only before:** (1) database role/membership/privilege/grant changes; (2) destructive or irreversible operations on established data/resources; (3) production, real personal data or shared runtime/data environments; (4) changing established authentication, authorization, cookies/session, CSRF/CORS, tenancy/RLS, protected CI/ruleset or accepted security architecture policy; (5) merging into `develop` or releasing to production. Implementing already-approved behavior under existing policy is routine. If some higher-priority installed rule actually denies an operation, stop that operation and ask for the smallest policy/permission change; never bypass it.

## Resources and reporting

Use isolated synthetic data and owned disposable test containers; verify scoped cleanup, not global prune. Do not change retained owner databases, secrets, certificates or unrelated services without the applicable slice authority. Never place credentials, sensitive user data or raw secrets in source, PRs or logs.

Report on one screen in Linear: **what users can now do; positive/negative tests passed or failed; what remains blocked or unexecuted; one PR link**. Keep verbose scanner/test receipts and hashes in CI or approved artifacts. Do not call a slice complete from compilation alone, a test-only fixture or an unverified browser action.

<!-- sonar:begin:codex-secrets-on-read -->
# SonarQube secrets scanning for files protocol

Before reading any file in this workspace, scan it for secrets with the deterministic scanner:

```bash
sonar analyze secrets <path/to/file>
```

If the command reports that the file contains a secret, **do not read the file**. Instead:

1. Inform the user that the file appears to contain a secret or credential and that reading it would expose the value in chat history, logs, and any downstream telemetry.
2. Advise them to rotate the leaked credential at its source of truth and remove it from the file.
3. Do not proceed with the original request until the secret has been removed.
<!-- sonar:end:codex-secrets-on-read -->
