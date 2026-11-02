# M1 owner-assisted worker rotation

This candidate implements the owner-approved, isolated LocalStack path from
ZEK-17 approvals fb4cefbe and handoff 54886786. It does not admit application
startup. Chief must independently review the new security changes.

## Source and configuration boundary

The Auth API reuses its admitted Identity, signing-key and published-key state.
The existing first-admin command and browser session policy remain separate.
Only the configured genuine owner may operate on the configured existing M1
organisation. The two fixed profiles are adminarea-retry and client-projection.
Each receives its own passwordless subject, current least-privileged membership,
real stored session and signed access token. Automatic issuance/refresh is absent.

The access-token duration is sixty minutes from actual issuance. The stored
session idle and absolute deadlines are sixty minutes from genuine creation.
They are independent deadlines: effective access ends when either expires.
Neither policy backdates the JWT or extends the issuer's existing one-hour ceiling.

The four JSON files in configuration/worker-rotation-preparation are incomplete
preparation documents, not runnable appsettings files. Their switches are OFF.
Resolve every required input from its approved source before constructing the
four real protected files. The existing 120-second processing and 86400-second
retention values come from the first-admin operator; they are operation limits,
not worker expiry policy. Do not create the four static settings volumes yet.

## Dual cutover and recovery

1. Prepare both worker outcomes in one Auth transaction. Old sessions remain
   usable. A lost secret response requires inspection/recovery, never blind remint.
2. Durably stage both private envelopes outside Git, build contexts and logs.
3. Commit begin-cutover before the first rotation-volume publication.
4. Publish each service's complete envelope using its own rotation-only volume.
   Use atomic replacement of current.json inside a stable directory; an isolated
   file mount may retain the old inode and is insufficient.
5. Obtain both publication receipts and actual successful-use witnesses, with
   unchanged container identity and process instance. A copy alone is not adoption.
6. Finalize old-session revocation only after both workers meet those conditions.

Before cutover, abort may revoke an unused prepared pair. After publication may
have begun, partial/uncertain delivery is degraded recovery, not safe abort.
Preserve old sessions until both new workers are witnessed. Inspect the same
operation after interruption; do not automatically generate another pair.
There is no atomic transaction spanning PostgreSQL and two Docker volumes.

## Protected runtime delivery

Each worker reads its own protected file on each relevant cycle/event. It retains
no long-lived token fallback. A file must pass identity, ownership, mode, complete
encoding, checksum, profile, scope, lifetime and monotonic-generation checks.
A file cannot authenticate itself: existing Auth signature, session and current
organisation authority checks remain required before business persistence.

Consumer directory mounts are read-only at /run/zeka-worker-credential. Consumer
UID/GID are 1654, capabilities are dropped and no-new-privileges is required.
The files are 0400; stable private directories are 0500 and owned 1654:1654. Only an
admitted short-lived publisher may temporarily make its own directory writable,
flush and replace the child atomically, then restore 0500. Failed helpers and
uncertain publication evidence are preserved for inspection.

The five sealed TLS/JWT/CA volumes and four future static settings volumes are
separate. Do not reown/unseal/repurpose them. Do not share rotation volumes across
services, mount a Docker socket in applications, or retain Windows secret binds.

## Evidence and admission limitations

No application, database or Docker operation is authorized merely by running
source tests. Before runtime validation, independently admit exact source/image,
Windows identity, engine, resource labels/IDs, protected inputs and migration state.
Do not start historical resources or replay mismatched migrations.

The base Compose logging driver is none. This candidate must leave adoption
pending when a bounded sanitized successful-use witness is unavailable. Do not
silently enable retained raw application logs to manufacture a witness. Compare
Docker's host process identity before/after and the application's namespace process
ID plus process-instance marker separately; those PID namespaces differ.

A first issue before workers start is pending first use. A quiet Client without a
real event is pending adoption. Do not synthesize business work or restart workers
just to obtain a receipt. Unit tests, configuration parsing, protected runtime
proof and CI are separate claims. C1 remains 0/7, CONFIGURATION_READY BLOCKED and
C1_TLS_PASS NOT_RUN until independently evidenced. No merge or release is included.


## Explicit recovery limit for review

If a cutover has started and its new sessions expire before both uses are
witnessed, this version requires an owner-reviewed recovery disposition.
Inspection retains CUTOVER_STARTED and its deadlines; finalization refuses
expired families, abort refuses a possibly adopted pair, and another prepare
refuses the unresolved operation. This is deliberate fail-closed behavior,
not complete recovery support. Do not reset the ledger or fabricate adoption.
Chief must assess this limitation before runtime admission.
