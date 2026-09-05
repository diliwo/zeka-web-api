# ADR-003 tenant persistence migration

This document is the implementation inventory and rollout contract for issue #43. `OrganisationId` values always come from AuthManagement; downstream services never derive them from tenant names.

## Entity classification

| Service | Classification | Persisted entities |
| --- | --- | --- |
| AuthManagement | Platform-global | `ApplicationUser`, Identity roles/claims/logins/tokens, `PermissionSet`, `IdempotencyRecord` |
| AuthManagement | Tenant registry | `Organisation`, `OrganisationMembership` |
| AuthManagement | Mixed-scope operational | `AuditEntry`, `OutboxMessage` (nullable `OrganisationId` is intentional for platform operations) |
| AdminAreaManagement | Tenant-owned | `Team`, `StaffMember`, `Partner`, `DocumentPartner`, and Partner-owned contact/email rows |
| AdminAreaManagement | Reference data | `City`, `Nationality`, `Profession`, `School`, `Training`, `TrainingField`, `TrainingType` |
| ClientManagement | Tenant-owned | `Client`, `SocialWorker`, `SocialCase`, `Assessment`, `ProfessionalAssessment`, `ProfessionnalExperience`, `SchoolRegistration`, `MonitoringReport`, and Client-owned value-object columns |
| ClientManagement | Reference data | `Language`, `MonitoringAction`, `NatureOfContract`, `Profession`, `School`, `Training`, `TrainingField`, `TrainingType`, `EmploymentTerminationType` |

Reference data is shared and contains no tenant security key. Owned/value-object rows inherit the classification and organisation identity of their owner.

## Constraints

Tenant-owned entities have a non-nullable `Guid OrganisationId` and an alternate key over `(Id, OrganisationId)`. Relationships between tenant-owned entities use composite foreign keys containing both identifiers, which makes cross-organisation relationships invalid at the database boundary.

Natural-key uniqueness is scoped as follows:

- AdminAreaManagement: `(OrganisationId, Team.Acronym)`, `(OrganisationId, StaffMember.UserName)`, and `(OrganisationId, Partner.PartnerNumber)`.
- ClientManagement: `(OrganisationId, Client.ReferenceNumber)` and `(OrganisationId, SocialWorker.UserName)`.

## Required remediation input

Before either downstream migration runs, create this table in that service-owned database and populate it only with mappings approved against AuthManagement:

```sql
CREATE TABLE "__OrganisationTenantMap" (
    "TenantName" text PRIMARY KEY,
    "OrganisationId" uuid NOT NULL UNIQUE,
    CONSTRAINT "CK_OrganisationTenantMap_NonEmpty"
        CHECK ("OrganisationId" <> '00000000-0000-0000-0000-000000000000')
);
```

Every distinct legacy `TenantName` on a tenant-owned table must map to exactly one AuthManagement organisation. Stop remediation when a name is blank, maps ambiguously, has no corresponding organisation, or when multiple names are proposed for one organisation without explicit approval. Keep the reviewed mapping export with the deployment evidence.

Before migration, detect natural-key collisions:

```sql
SELECT "TenantName", "UserName", count(*) FROM "StaffMembers"
WHERE "UserName" IS NOT NULL GROUP BY "TenantName", "UserName" HAVING count(*) > 1;
SELECT "TenantName", "Acronym", count(*) FROM "Teams"
GROUP BY "TenantName", "Acronym" HAVING count(*) > 1;
SELECT "TenantName", "PartnerNumber", count(*) FROM "Partners"
GROUP BY "TenantName", "PartnerNumber" HAVING count(*) > 1;
SELECT "TenantName", "ReferenceNumber", count(*) FROM "Clients"
WHERE "ReferenceNumber" IS NOT NULL GROUP BY "TenantName", "ReferenceNumber" HAVING count(*) > 1;
SELECT "TenantName", "UserName", count(*) FROM "SocialWorkers"
WHERE "UserName" IS NOT NULL GROUP BY "TenantName", "UserName" HAVING count(*) > 1;
```

The migrations fail before changing schema when these collisions exist. Resolve each result through reviewed business input that identifies the record to rename or merge, the replacement natural key, the approving owner, and the corresponding `OrganisationId`. Record that input as a deployment artifact and execute it as a distinct remediation step; never hide it in generic migration setup.

## Compatibility and forward rollout

1. Back up each service database and record the applied EF migration.
2. Inventory distinct legacy names and row counts per table. Resolve them against immutable AuthManagement IDs.
3. Create and populate `__OrganisationTenantMap` in each service-owned database.
4. Quiesce legacy writers because they can only emit tenant names. This is the compatibility boundary until issues #30 and #44 provide the new registry and explicit tenant context.
5. Apply `OrganisationTenantConstraints`. The migration adds and backfills IDs before removing names; it aborts on a missing mapping, an unmapped row, or `Guid.Empty`.
6. Validate row counts, non-null IDs, tenant-aware unique indexes, and composite foreign keys.
7. Deploy the shared-connection application build. Do not re-enable writers until the #30/#44-compatible producer supplies immutable organisation identity.
8. Retain `__OrganisationTenantMap` through the validation window, then archive and remove it under an approved operational change.

## Rollback and recovery

Rollback requires a maintenance window because the legacy application cannot safely accept writes made only with `OrganisationId`.

1. Stop writers and restore the pre-migration backup for the safest rollback.
2. Automatic EF rollback is deliberately blocked because converting immutable IDs back to mutable names is not losslessly reversible.
3. Restore the verified pre-migration backup. If post-migration writes occurred, reconcile them by immutable `OrganisationId`; never invent a display name for reverse mapping.
4. Re-run row-count, orphan, duplicate, and foreign-key checks before reopening traffic.

Database-per-tenant routing and tenant-specific connection selection are not part of this model. Each service uses its own single shared PostgreSQL connection. Registry/configuration removal remains owned by #30, EF query/write enforcement by #44, RLS and runtime roles by #45, and the final production gate by #46.
