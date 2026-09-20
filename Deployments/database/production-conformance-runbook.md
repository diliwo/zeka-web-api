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
2. Record the service, revision, migration identifier, bootstrap phase and redacted error category.
3. Reset any assumed owner role, close the failed migrator session and determine whether the transaction committed.
4. Rerun the idempotent reviewed bootstrap only to reconcile its declared roles, memberships, settings, default grants and ACLs.
5. Production migration targets are forward-only `latest`. A down-migration target is prohibited; use a reviewed forward repair or the database-wide restore process. This is currently an operational rule, not executable OPS-03 evidence: no accepted production migration entry point enforces the target before database mutation.
6. After a successful forward repair, rerun the complete migration chain on a fresh database, the supported upgrade checkpoint, runtime identity validation and all v10 manifest checks.

## Database-wide backup and restore prerequisites

The supported prerequisite check verifies version-aligned PostgreSQL `pg_dump` and `pg_restore` tooling against a manifest-green database. Before an authorized database-wide backup/restore exercise, also verify encryption and access controls in the approved environment, available destination capacity, migration compatibility, and a post-restore v10 catalog check for all three service databases.

Shared-database point-in-time recovery is database-wide. Per-tenant restoration requires logical reconstruction and reconciliation and is not promised by this procedure. Tenant movement is not implemented here.

## ADR-008-blocked commitments

RTO, RPO, backup retention, regional recovery and tenant movement remain ADR-008-blocked. This runbook deliberately provides no values, topology or service-level commitment for them.
