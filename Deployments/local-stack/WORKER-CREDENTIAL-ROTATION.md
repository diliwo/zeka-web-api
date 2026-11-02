# M1 owner-controlled worker rotation

Owner decision bf0cc74a and handoff e956b00a replace the earlier usage-confirmation
cutover design. This is a source candidate for Chief's one independent review
on the pushed draft head. It does not authorize runtime startup or C1 acceptance.

## Fixed authority and lifetimes

Auth keeps its signing-key and published-key custody. The existing first-admin
command and browser policy remain separate. Only the configured genuine owner
may issue for the configured existing M1 organisation. The two fixed profiles
are adminarea-retry and client-projection. Each has a distinct least-privileged
passwordless subject, membership, genuine stored session and signed access JWT.
Access JWTs expire sixty minutes from issuance; stored session idle and absolute
deadlines are sixty minutes from genuine creation. Effective access ends when
either deadline expires. Automatic issuance and refresh are absent.

M1WorkerRotation is OFF by default and LocalStack-only. Its RegistryId must match
the independently admitted protected operator registry, alongside the exact real
OwnerSubjectId and OrganisationId. No generated identity placeholder is usable.
The preparation JSON files remain incomplete, disabled documents, not runtime
appsettings. The four static settings volumes remain unopened.

## Forward-only owner command

1. Each explicit owner invocation uses a fresh operation identity and prepares
   both real sessions in one Auth transaction. Replaying the same operation never
   remints a lost private response. A later explicit run may issue a fresh pair.
2. Durably stage both Auth-hash-bound envelopes outside Git and build contexts.
3. Atomically replace current.json in each service's distinct rotation volume.
   Verify both actual resulting files, generations, hashes, ownership, modes and
   actual read-only consumer mount scopes. Two volumes are not one transaction.
4. Only after BOTH deliveries pass, call Auth revoke-others. In one transaction,
   preserve each newly delivered session and revoke all other active families of
   those exact two worker identities. Never include the owner or another identity.
5. Return success only when revocation is confirmed. A partial publish performs
   no old-session revocation. A failed/uncertain revocation is not global success.
   Another explicit owner run moves forward with a fresh generation; there is no
   usage-confirmation wait, CUTOVER_STARTED veto or automatic renewal ceremony.

Concurrent owner runs are serialized. Destination and provider generation fences
reject rollback. Failed helper evidence is preserved. A still-running exact-owned
rotation helper must be safely stopped and reverified before another publisher;
foreign or unverifiable helpers and unexpected protection modes block execution.
No historical W3 helper is eligible for that bounded operation.

First issuance before admitted consumer mounts exist may verify files but remains
mount-scope pending, without revocation. This is delivery admission, not use/adoption.

## Authorization retry and independent adoption proof

A worker authorization request returning 401 rereads the protected credential and
retries once within the same operation. One operation-local budget is shared by
tenant-access and Client membership calls. A second 401 is transient/unavailable;
403 is denied without retry. Existing 503 retry allowance and two-second deadlines
remain. The worker-only 503-then-401 edge may use three HTTP sends (including the
explicit one-time401 retry) within that same deadline. Nonworker behavior stays
unchanged. No business message or handler is retried locally.

After revocation, independent runtime evidence must prove unchanged container ID,
main PID/start identity, real Auth denial of the old session, successful meaningful
synthetic business work with the replacement, and one operation already in flight
at revocation per worker. Copy success is not adoption. Raw logs are unnecessary;
logging.driver=none is preserved. Those runtime observations remain NOT_RUN.

Client runtime acceptance additionally waits for accepted, pinned and actually
adopted EventBus 1.3.0 (or the qualifying approved 1.3.0-rc.1 exception). Prove the
same original EventId survives a remaining transient 401 through recoverable broker
redelivery, no ACK before durable success, one durable effect, and no rotation-only
exhaustion of delivery limit 5 into DLQ. No EventBus/library, DLQ, topology, delivery
limit or worker-local message-retry workaround is included. ZEK-22 remains blocked.
AdminArea can earn its separately admitted runtime evidence first.

## Protected delivery and retained evidence

Consumers read their own complete protected file per operation through a read-only
directory mount at /run/zeka-worker-credential. UID/GID 1654, cap-drop ALL and
no-new-privileges remain required; files 0400 and private directories 0500. Only an
exact admitted short-lived publisher receives RW access. Atomic child replacement
preserves the mounted directory; file-subpath replacement is insufficient.

Five sealed TLS/JWT/CA volumes, host-only CA, four pending static settings volumes,
original database/network resources, frozen TLS source, EventBus branches and W1/W2
history remain preserved. No private inputs or runtime resources are admitted by
source tests. The original 712a34a patch and 107 tests stay historical. Original
EXACT_ACTUAL_USE_EVENT remains a historical synthetic observability failure. A new
matched-source reproduction proves the old parser fails on normal PowerShell 7.4
because DateKind is unsupported, while the app's 7.6 host accepts it. The historical
binary version was not captured, so attribution remains explicitly qualified. The
successor preserves date strings without that option and tests the installed 7.4
executable. Chief must assess this explanation before C1 EVIDENCE closure; the
original failure receipt stays unchanged. C1 remains 0/7, CONFIGURATION_READY BLOCKED
and C1_TLS_PASS NOT_RUN. No merge or release is authorized.
