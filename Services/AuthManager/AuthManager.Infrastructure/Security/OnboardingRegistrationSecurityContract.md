# Auth onboarding registration: security manifest contract

This document records the classification of exactly four Identity enrollment tables in `rls-manifest.v20.json`, targeting the existing migration `20261115195218_OnboardingRegistration`. It applies the accepted tenant-neutral Identity boundary; it does not exempt future Auth tables or organisation-owned data from tenant isolation.

## Accepted authority and scope

The accepted [ADR-003 — Multi-tenancy and Data Isolation Strategy](https://github.com/diliwo/ubongo/blob/e182573a1ab503a151f3ffe1fd71bb0e32e8d17f/projects/Triptych/zeka%20saas/Docs/Architecture/ADRs/ADR-003%20-%20Multi-tenancy%20and%20Data%20Isolation%20Strategy.md) separates tenant-neutral Identity from tenant-owned data. The accepted [PostgreSQL RLS and Runtime Role Separation](https://github.com/diliwo/ubongo/blob/e182573a1ab503a151f3ffe1fd71bb0e32e8d17f/projects/Triptych/zeka%20saas/Docs/Architecture/PostgreSQL%20RLS%20and%20Runtime%20Role%20Separation.md) requires explicit classification, exact privilege inventories and separate owner, migrator and runtime identities. Documentation revision: `e182573a1ab503a151f3ffe1fd71bb0e32e8d17f`.

Registration precedes organisation creation. These four tables have no `OrganisationId`, tenant ownership or membership. Their access paths belong exclusively to Auth's user enrollment flow. They are explicit `excludedTables` entries, not tenant tables. They are not added to `globalControlPlaneTables`, which is the existing lifecycle-registry subclass.

### EmailVerificationChallenges

[RegistrationService.cs](../Onboarding/RegistrationService.cs) creates and validates challenges for an Identity user during register/resend/exchange. The [entity](../Persistence/Entities/EmailVerificationChallenge.cs) binds a user, generation, email/security-stamp bindings, token hash, expiry and transition state. It does not identify or authorize an organisation.

### VerificationMailOutboxMessages

[VerificationMailDispatcher.cs](../Onboarding/VerificationMailDispatcher.cs) reads delivery work by joining challenge to Identity user. The [entity](../Persistence/Entities/VerificationMailOutboxMessage.cs) contains challenge/generation/operation and delivery state. It carries no tenant payload or tenant authority. Its exclusion follows the user-verification lifecycle, not the fact that it is an outbox.

### OnboardingGrants

[RegistrationService.cs](../Onboarding/RegistrationService.cs) validates a restricted, user-bound credential using purpose, expiry, user confirmation/status and security stamp. The [entity](../Persistence/Entities/OnboardingGrant.cs) supplies no existing tenant membership or tenant API authority. This classification grants no additional consumption or update privilege.

### OnboardingOperationReceipts

[RegistrationService.cs](../Onboarding/RegistrationService.cs) writes an immutable commit witness to reconcile uncertain register/resend/exchange outcomes. The [entity](../Persistence/Entities/OnboardingOperationReceipt.cs) links user/challenge/outbox/grant before organisation creation. It is not an organisation audit or tenant business record.

## Least-privilege contract

The existing [Auth bootstrap contract](../../../../Deployments/database/bootstrap-auth-roles.sql) is the source of these exact permissions:

| Table | Runtime table privileges | Runtime column UPDATE |
| --- | --- | --- |
| EmailVerificationChallenges | INSERT, SELECT | Status, TransitionedAtUtc, Version |
| VerificationMailOutboxMessages | INSERT, SELECT | Attempts, DeliveredAtUtc, LeaseExpiresAtUtc, LeaseId, NextAttemptAtUtc, ObsoleteAtAcceptance, Outcome, Status, Version |
| OnboardingGrants | INSERT, SELECT | None |
| OnboardingOperationReceipts | INSERT, SELECT | None |

Owner remains `zeka_auth_owner`; the restricted runtime remains `zeka_auth_runtime`. No DELETE, table-wide UPDATE, grant option, sequence privilege, owner membership, DDL/BYPASSRLS authority or JWT authority is added. Existing roles, classifications, policy expressions, lifecycle subclasses and default-deny privileges remain unchanged.

The [existing migration](../Migrations/20261115195218_OnboardingRegistration.cs) adds no RLS policies or grants. This manifest declares the expected global-table/privilege state; it does not itself change a database. A separately approved database checkpoint must inspect/apply only the accepted narrow grant contract and prove actual ownership, schema and privileges. The broad bootstrap must not be run merely to satisfy this manifest.

## Manifest admission and evidence boundary

Only v20 is embedded by the Auth project; v19 and earlier files remain historical. The loader retains its exact-one-resource requirement. The owner-approved compatibility extension admits schema 20 only for AuthManagement, rejects unknown versions and preserves complete classification, migration-history, catalog, RLS and role checks. The twelve new column entries and four table privilege rows are exact allowlists.

Source-only tests establish model/resource/invariant behavior. Controlled catalog fixtures are not PostgreSQL execution evidence. This source correction authorizes no migration, protected configuration/key change, container operation, registration or acceptance advancement.
