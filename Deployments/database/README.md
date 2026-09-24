# PostgreSQL runtime-role lifecycle

These procedures implement the provider-neutral Issue #45 boundary. Environment-specific Azure secret delivery and Workload Identity are deferred to ADR-005. Never place passwords or connection strings in this repository, migration arguments, command lines, or retained logs.

## Provision and migrate

For each service database, Zeka Platform Operations:

1. creates the database using the separately authorized PostgreSQL administrative process;
2. executes the matching `bootstrap-*-roles.sql` script to create/reconcile the service `owner`, `migrator`, and `runtime` roles, ownership, membership, default privileges, and database/schema ACLs;
3. supplies the deployment-only migrator credential through the approved host secret-delivery mechanism;
4. builds the standalone `Tools/Zeka.DbMigrate` CLI at the reviewed SHA and invokes
   `plan` or `apply` for one closed service descriptor and `latest` or an exact
   compiled migration identifier; the host supplies the matching migrator connection
   only through `--credential-stdin`, never through arguments or retained files;
5. after a successful forward migration, separately reruns the reviewed bootstrap
   under the role-administrator credential, which is never supplied to the CLI;
6. executes the versioned manifest verifier using the migrator-to-matching-owner path,
   then performs runtime readiness using only the matching runtime identity;
7. retains only the sanitized atomic CLI result, reviewed digest and verification evidence.

Application processes never apply migrations at startup. The owner is `NOLOGIN`; runtime workloads receive only their matching runtime credential. Owner/migrator may perform required DDL and RLS/policy management, but are not added to runtime RLS policies for protected-row DML. RLS must not be disabled or bypassed for convenience. A future protected-data backfill requires a separately reviewed tenant-aware or explicitly privileged procedure.

## Runtime readiness

Each service validates on startup that its configured database identity is exactly the matching restricted runtime role: `LOGIN`, `NOSUPERUSER`, `NOBYPASSRLS`, `NOCREATEDB`, `NOCREATEROLE`, `NOINHERIT`, `NOREPLICATION`, no memberships, and no role/database settings. Readiness fails closed when this validation or connectivity fails.

## Single-login password rotation

PostgreSQL stores one password verifier for each runtime `LOGIN` role. Replacing it invalidates the old password for new authentications. Existing authenticated sessions may temporarily continue, but this is session continuity—not simultaneous old/new credential overlap. Issue #45 does not promise overlap and does not introduce dual-login or capability identities.

Zeka Platform Operations performs rotation in this order:

1. verify the current catalog manifest and capture redacted pre-change evidence;
2. replace the password of the existing bounded runtime role without changing any role attribute, membership, grant, ownership, or policy;
3. update the workload credential through the approved provider mechanism;
4. drain or restart every workload instance so old authenticated pool sessions cannot remain authoritative;
5. require runtime-identity validation, readiness, a fresh authentication, and catalog-manifest verification to pass;
6. retain redacted outcome evidence and close the rotation only after all instances are ready.

If replacement or readiness fails, determine which ordered step completed, finish or repeat workload credential delivery and pool restart using the current password, and escalate through the time-bounded emergency process when service cannot be restored. Failure handling must never widen runtime grants, add membership, enable `BYPASSRLS`, disable RLS, repurpose owner/migrator credentials, or claim that the previous password remains valid for new sessions.

## Revocation and emergency access

Revocation disables or replaces the affected login credential, drains workload pools, verifies that new authentication with the revoked credential fails, and reruns readiness and manifest verification. Emergency access uses a separately authenticated, time-bounded operator process approved by Zeka Platform Operations. It must not repurpose runtime credentials or create a generic platform RLS bypass. Retained evidence identifies the authorization, database/service, operation, time, and result without secrets or tenant payloads.

Rollback never restores service by broadening runtime privilege or disabling RLS. Any incident action that changes the accepted role/RLS boundary requires explicit approval and a separately retained audit record.

Production deployment is forward-only. The CLI validates exact identity, membership,
owner assumption, advisory locking and the known migration-history prefix before it
classifies `DOWN_TARGET_REJECTED`, `ALREADY_CURRENT`, or `FORWARD_APPROVED` and before
EF migration can start. A down target is never executed; recovery uses a separately
reviewed forward repair or an authorized database-wide restore. The Issue #46 target
inventory and incident procedures are in `production-conformance.targets.json` and
`production-conformance-runbook.md`.
