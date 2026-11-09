# W3 local-only broker checkpoint

Select `compose.w3-broker-check.yaml` explicitly for the disposable, synthetic
`zeka-m1` broker check. It starts only RabbitMQ. It does not start applications,
initialize databases, seed organisations or prove staff projection.

The local guest exception is confined to this selected profile. It has no
published ports, host networking or external/shared network. Do not use it in
TEST, staging or production. TEST still requires no guest account, no loopback
override and configurable application credentials. This file does not add
credential options to the existing EventBus package.

Before creating resources, verify that any existing project containers are
owned by the current W3 run. Never restart, replace or remove historical
containers or import the historical root Compose files.

From the repository root:

```powershell
docker compose --project-name zeka-m1 -f Deployments/local-stack/compose.w3-broker-check.yaml --profile w3-broker-check config --quiet
docker compose --project-name zeka-m1 -f Deployments/local-stack/compose.w3-broker-check.yaml --profile w3-broker-check up -d rabbitmq
```

A successful health check is insufficient. The checkpoint built a disposable
probe against the real Client API project and its existing
Zeka.Extensions.EventBus 1.2.0 dependency, published the Client assemblies in the
pinned .NET 8.0.29 runtime image, and used the package's RabbitMqConnection.
It opened an authenticated connection and channel on `/`, declared an exclusive
synthetic queue, confirmed publication and read the same payload back. It did
not construct ConnectionFactory directly or start the whole Client API.
Source, native results and disposable probe inputs are retained with the W3
Linear handoff. No second seed or evidence framework is introduced.

## Seed entry prerequisite

The standard `compose.yaml --profile applications` interface still requires
`ZEKA_M1_PRIVATE_DIR` pointing outside this checkout to admitted protected
configuration, credentials, signing/session keys and CA trust. Do not print
resolved secret configuration or put it in tracked files or image layers.

W3 must use the real Auth authority and AdminArea staff command/outbox through
RabbitMQ to the Client consumer. The existing Auth authority checks include
active membership/permissions and an active stored browser session identified
by the worker bearer's `sid` claim. Do not substitute a fabricated JWT, fake
authority or direct Client projection insertion.

Only fresh initialization may seed the two fictional organisations and record
completion. Ordinary restart verifies existing seed/projection state and never
seeds or republishes. Partial initialization must fail explicitly; reset remains
the separate W4 operation.
