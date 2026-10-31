# Provisional staff-projection EventBus adoption

Chief-authored source only; Codex independently reviews and integrates. Base: PR101
`a548ca8979efd54031af8db51a088ec6ddf94b51`. Library source:
`cbf410c15d105d4b3eca9aa03a83718c488da014`. Neither this source reference nor a local
build is a reviewed/distributed package or accepted C1 integration evidence.

## Shared contract and startup

Both service Infrastructure projects link `Shared/StaffProjectionTransport.cs`.
That one definition owns versioned durable source/exchange/DLQ names, exact routes
and finite capacities. The library owns quorum type, delivery limit5, prefetch10,
at-least-once DLX and reject-publish declarations/verification. The existing
SocialWorkerCreated handler route is retained; historical queues are never reset,
deleted or redeclared in place. Conflicting service-local topology fails closed.

The .NET8 hosted lifecycle initializes this same topology in StartingAsync, before
any hosted publisher/consumer starts, regardless of hosted-service registration order.
This allows AdminArea to provision the intended Client queue before Client runs.
Initialization failure prevents hosted publication; the publisher also verifies exact
readiness for every send. Client selects the shared new queue, not its historical name.

The additive `compose.eventbus-provisional.yaml` pins RabbitMQ3.13.7 and supplies
the internal management API endpoint. Existing protected per-workload RabbitMQ
credentials and broker/management identity must match. This does not provision or
share credentials, alter networking/ports, perform a historical migration, or prove
actual TLS readiness. Do not run the overlay without the existing operator admission.

## Provisional build only

The two Application projects reference planned `Zeka.Extensions.EventBus 1.3.0-rc.1`
unless an explicit `ZekaEventBusSourceProject` MSBuild property selects the local
library project. For the source-only review, verify its Git HEAD/cleanliness equals
the pinned SHA before supplying its absolute Eventbus/Eventbus.csproj path. No
package is generated or published by this source override. The external project
path and provisional build command are local evidence inputs, not a committed secret.

Do not claim the unpublished package restores successfully. Codex must replace the
provisional source evidence with the Greptile-reviewed, authorized/distributed RC,
verified content identity and committed packages.lock.json/clean locked restore on
the final integration SHA before C1 acceptance. No automatic stable release/merge.

Library fail-stop contract: https://github.com/diliwo/eventbus-dotnet-lib/blob/cbf410c15d105d4b3eca9aa03a83718c488da014/docs/publisher-failstop-m1.md

## Outbox and evidence boundary

PublishedAtUtc is set only after an owned attempt completes successfully with CLOSED
state (worker, disposal and explicit owned raw-socket termination proven). The outbox
refuses before admission unless the publisher provides explicitly enabled M1 process
fail-stop. Caller cancellation requests Stop but never releases the transaction's
closure lock on an unproven timeout. A dedicated watchdog terminates the WHOLE shared
process if termination remains unproven; PostgreSQL then rolls back the uncommitted
transaction and closure waits for database-observed lock release.

The one-way OPEN → CLOSING → CLOSED lifecycle and one logical send commit point are
shared by ordinary publishing and raw replay. Topology/channel/network/cleanup work
is outside the short admission mutex. Every attempt owns a publisher-only connection
and irrevocable raw-socket custody, not the shared consumer connection. Already
committed/in-flight sends remain uncertain and can arrive after process death.
Original EventId and pending identity are preserved; duplicates require existing
Client business idempotency and closure guards. Late post-stop settlement is never
publication success. No schema, authority, lock-order or topology contract is changed.

### Precise M1 configuration proposal — not deployed

Only explicit owner-controlled M1 configuration may supply
`M1PublisherFailStop:EnableOwnerApprovedM1ProcessFailStop=true`,
`M1PublisherFailStop:AttemptDeadline=00:00:02`, and
`M1PublisherFailStop:TerminationGrace=00:00:02`. Defaults are OFF; no environment-name
inference or production activation. The native fatal reason is
`ZEKA_M1_PUBLISHER_TERMINALITY_UNPROVEN`. FailFast skips finally/async-log flush;
collect actual process/supervisor exits. Proposed M1 Docker policy is
`restart: "on-failure:3"`, with measured Docker restart backoff and bounded count.
Codex/operator owns deployment changes. No deployment file was modified here.

A managed watchdog is not a hard-real-time guarantee during OS/runtime suspension
or machine failure. Such scenarios need external supervisor termination evidence.
DB connection-loss detection can take longer than the publisher deadline. Never
claim CLOSED or release the fence based on local cancellation, Abort, timeout or
process-exit notification alone; closure waits on PostgreSQL's transaction lock.

### Evidence boundaries

`Tools/EventBus.FailStopProof` uses real disposable PostgreSQL/RabbitMQ with actual
AdminArea outbox, runtime role, transaction guard and closure gate. Test-only transport
wrappers force pre-open/topology/channel/pre-commit/post-commit and missing-confirm
faults. A post-commit wrapper stall is not a natural blocked driver/socket write.
Client duplicate/closure provider tests remain separately identifiable; a combined
live Auth/broker/Client/M1/supervisor run and distributed package/locks/protected CI
are not established by a passing source fixture. TLS acceptance stays separate.
