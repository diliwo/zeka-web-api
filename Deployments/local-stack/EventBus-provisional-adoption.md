# Provisional staff-projection EventBus adoption

Chief-authored source only; Codex independently reviews and integrates. Base: PR101
`a548ca8979efd54031af8db51a088ec6ddf94b51`. Library source:
`56881cb4982963c42f314aa481540a7bcc01064f`. Neither this source reference nor a local
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

## Outbox and evidence boundary

PublishedAtUtc is set only after PublishAsync completes successfully. Library return,
NACK, disconnect and uncertain-confirm exceptions leave the persisted row pending.
The existing two-second caller wait is retained; a late positive confirm can mean
accepted publication with a pending row. Retry keeps the serialized original EventId,
so actual Client business idempotency remains necessary. No transaction/schema,
tenant, closure-gate or authorization behavior is changed.

Focused tests prove shared composition/lifecycle ordering and the caller publication
boundary. Their fake broker and EF InMemory observations do not prove PostgreSQL
transaction/RLS behavior, real outbox persistence against RabbitMQ, Auth worker
authority, Client projection idempotency, or accepted end-to-end recovery. Those real
ZEK-23 tests and the combined oldest-pending-outbox-age/DLQ-depth smoke remain due
on the reviewed immutable integration SHA. Owner-Windows TLS/CA/hosts/trust proof
also remains separate and C1-critical at the4November gate.
