# Production conformance incident procedures

This runbook is the provider-neutral Issue #46 platform procedure. It preserves the accepted owner/migrator/runtime separation, trusted transaction-local tenant context, first-SQL enforcement, RLS and v10 manifests. Evidence must contain service, deployment revision, attempt identifier, transaction marker, backend PID, timestamps and outcome only; do not retain credentials, connection strings, tenant identifiers or payloads.

## Runtime identity mismatch

1. Fail readiness and stop new work for the affected service instance.
2. Record the expected role, observed role name, service, revision and correlation only. Do not record credential material.
3. Verify that the workload received its matching runtime credential and did not receive owner, migrator, administrative or cross-service credentials.
4. Drain the affected pool and restart with the corrected runtime credential.
5. Require runtime identity validation and the complete v10 manifest check before restoring readiness.
6. Escalate any observed superuser, `BYPASSRLS`, owner, migrator, inherited membership or unexpected capability immediately. Never widen grants to restore service.

## Catalog drift

1. Treat the failing bidirectional manifest comparison as authoritative evidence and stop deployment/readiness.
2. Retain the redacted drift category and object identity; do not apply ad-hoc grants or policy edits.
3. Compare the live catalog with the reviewed v10 manifest and migration history at the exact revision.
4. Restore only by rerunning the reviewed bootstrap for role/default-grant drift or by applying a separately reviewed forward migration for owned schema objects.
5. Rerun the v10 verifier and all negative drift probes. An unexpected additional privilege is a failure, not a compatibility exception.

## Pool-contamination suspicion

1. Stop new traffic to the affected instance and drain its database pool.
2. Preserve non-sensitive correlation: service/revision, attempt ordinal, transaction marker, backend PID, termination path and timestamps. Never log organisation identifiers or row data.
3. Reproduce with bounded pool sizes one and two, including commit, rollback, exception, timeout, cancellation and saturated waiters.
4. Require no-context failure after reuse, then fresh initialization before the first tenant SQL of the next attempt.
5. If a physical connection was discarded, prove a different PID and complete reinitialization. Do not require unsafe reuse merely to make the test pass.
6. Keep the service unavailable if any foreign observation, mismatched write or uninitialized tenant SQL is observed; return the evidence for security classification.

## Migration or bootstrap failure

1. Stop the deployment. Application runtimes must not execute migrations and must not receive the migrator credential.
2. Preserve the sanitized `zeka-db-migrate` result with its service, exact source SHA, operation UUID, migration identifiers, phase states and redacted error code. Never retain the credential, connection string, host, tenant data, raw SQL or unrestricted exception text.
3. A `DOWN_TARGET_REJECTED` result is a policy stop: migration and bootstrap must both be `not_started`. Compare the before/after history and catalog/privilege digest and do not continue the deployment.
4. `ALREADY_CURRENT` is a successful no-mutation outcome only when the read-only v10 manifest verification passes.
5. `MIGRATION_STATE_REQUIRES_INSPECTION` means EF work may have started. Reset the assumed role, close the failed session, inspect migration history and catalog state, and determine which commands committed. Do not blindly retry, run a down migration, mark readiness green or widen privileges.
6. A retry always reacquires the database-scoped advisory lock, revalidates identity and membership, and reclassifies the observed history. Continue only when the history is a known reviewed prefix and the requested operation is still forward; otherwise retain the failure and escalate for a reviewed forward repair or authorized database-wide restore.
7. Bootstrap reconciliation is a separate role-administrator phase outside the CLI. Rerun it only after successful migration to reconcile its declared roles, memberships, settings, default grants and ACLs. A bootstrap or verifier failure does not roll back an already committed migration.
8. After a successful forward repair, rerun the complete migration chain on a fresh database, the supported upgrade checkpoint, runtime identity validation and all v10 manifest checks.

The deployment host builds `Tools/Zeka.DbMigrate/Zeka.DbMigrate.csproj` from the
reviewed immutable SHA with that SHA supplied as `ZekaSourceSha`. It invokes only
`plan` or `apply`, one of `adminarea`, `auth`, or `client`, `latest` or an exact
compiled migration identifier, an operation UUID, and a destination beneath an
authorized host-controlled evidence root. It supplies the matching service-migrator
connection through `--credential-stdin`; the role-administrator credential never
enters this process. Concrete cloud, AKS, identity-provider and secret-delivery
topology remains deferred to ADR-004/ADR-005.

## Database-wide backup and restore prerequisites

The supported prerequisite check verifies version-aligned PostgreSQL `pg_dump` and `pg_restore` tooling against a manifest-green database. Before an authorized database-wide backup/restore exercise, also verify encryption and access controls in the approved environment, available destination capacity, migration compatibility, and a post-restore v10 catalog check for all three service databases.

Shared-database point-in-time recovery is database-wide. Per-tenant restoration requires logical reconstruction and reconciliation and is not promised by this procedure. Tenant movement is not implemented here.

## ADR-008-blocked commitments

RTO, RPO, backup retention, regional recovery and tenant movement remain ADR-008-blocked. This runbook deliberately provides no values, topology or service-level commitment for them.
