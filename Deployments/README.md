# Deployments

This directory contains Zeka’s local build helpers, PostgreSQL bootstrap and conformance files, Kubernetes manifests, and the standalone M1 Docker Compose stack.

See the [M1 local stack guide](local-stack/README.md) for configuration and startup prerequisites and the [database guide](database/README.md) for PostgreSQL provisioning.

## Local build helpers

| File | Purpose |
| --- | --- |
| [`build/local/dev.ps1`](build/local/dev.ps1) | Provides PowerShell commands to build, start, stop, rebuild and view logs for the Compose project. Its `down` command removes Compose volumes. |
| [`build/local/dev.sh`](build/local/dev.sh) | Provides Bash Compose build and lifecycle commands with repository-root project selection and a rootless Docker socket fallback. |
| [`build/local/.op/local.env`](build/local/.op/local.env) | Declares 1Password-backed inputs used by the local Compose build commands. |
| [`build/local/.op/nuget.env.example`](build/local/.op/nuget.env.example) | Provides an example environment template for GitHub Packages authentication during NuGet restore. |
| [`build/local/docker/postgres/init/01-create-databases.sql`](build/local/docker/postgres/init/01-create-databases.sql) | Creates the Auth, Client and AdminArea databases during PostgreSQL initialization. |

## PostgreSQL bootstrap and conformance

| File | Purpose |
| --- | --- |
| [`database/README.md`](database/README.md) | Explains per-service PostgreSQL role provisioning, forward migrations, readiness, credential rotation and revocation. |
| [`database/application-conformance.targets.json`](database/application-conformance.targets.json) | Lists the required application conformance checks, their evidence mappings and the boundaries that remain blocked. |
| [`database/bootstrap-adminarea-roles.sql`](database/bootstrap-adminarea-roles.sql) | Provisions and reconciles AdminArea PostgreSQL roles, schema ownership and restricted grants. |
| [`database/bootstrap-auth-roles.sql`](database/bootstrap-auth-roles.sql) | Provisions and reconciles Auth PostgreSQL roles, schema ownership and explicit grants for Identity and lifecycle objects. |
| [`database/bootstrap-client-roles.sql`](database/bootstrap-client-roles.sql) | Provisions and reconciles Client PostgreSQL roles, schema ownership and restricted grants. |
| [`database/bootstrap-life05a-verifier-fixture.sql`](database/bootstrap-life05a-verifier-fixture.sql) | Provisions dedicated LIFE-05A verifier logins with scoped read access to the synthetic fixture. |
| [`database/life05a-verifier-assertions.sql`](database/life05a-verifier-assertions.sql) | Checks verifier login restrictions, fixture privileges, tenant isolation and denial of forbidden operations. |
| [`database/life05a-verifier-ci-fixture.sql`](database/life05a-verifier-ci-fixture.sql) | Creates synthetic schemas, a tenant-context function and tables for the LIFE-05A CI checks. |
| [`database/production-conformance-runbook.md`](database/production-conformance-runbook.md) | Provides incident procedures for runtime identity, database catalog drift, connection pool contamination, migration failures and backup/restore prerequisites. |
| [`database/production-conformance.targets.json`](database/production-conformance.targets.json) | Lists required production database checks and evidence for roles, connection pooling, privileges and operations. |

## Kubernetes manifests

| File | Purpose |
| --- | --- |
| [`k8s/adminareaapi/adminarea-api.yaml`](k8s/adminareaapi/adminarea-api.yaml) | Defines the AdminArea API workload, a database readiness wait and the internal and external API services. |
| [`k8s/adminareaapi/adminarea-configmap.yaml`](k8s/adminareaapi/adminarea-configmap.yaml) | Supplies the AdminArea API’s PostgreSQL and RabbitMQ configuration. |
| [`k8s/broker/rabbitmq.yaml`](k8s/broker/rabbitmq.yaml) | Defines a RabbitMQ deployment and services for AMQP and the management interface. |
| [`k8s/ingress/ingress.yaml`](k8s/ingress/ingress.yaml) | Defines NGINX routing from the local API hostname to the `zekaocelotgw` Service. |
| [`k8s/zekadb/zekadb.yaml`](k8s/zekadb/zekadb.yaml) | Defines the PostgreSQL 17 workload, persistent storage, health checks and services. It consumes the initialization ConfigMap and bootstrap Secret. |
| [`k8s/zekadb/zekadb-configmap.yaml`](k8s/zekadb/zekadb-configmap.yaml) | Supplies PostgreSQL initialization SQL for creating the Auth, Client and AdminArea databases. |
| [`k8s/zekadb/zekadb-secret.yaml`](k8s/zekadb/zekadb-secret.yaml) | Defines the PostgreSQL bootstrap Secret consumed by the database workload. |

## M1 local stack

The `zeka-m1` stack separates database setup, Auth HTTPS configuration, a RabbitMQ checkpoint and worker credential delivery into dedicated Compose files.

### Documentation

| File | Purpose |
| --- | --- |
| [`local-stack/README.md`](local-stack/README.md) | Describes the standalone `zeka-m1` project, its protected configuration, health checks and startup prerequisites. |
| [`local-stack/README.database-bootstrap.md`](local-stack/README.database-bootstrap.md) | Documents the isolated Auth, AdminArea and Client database bootstrap, including repeatability, protected inputs and failure handling. |
| [`local-stack/W3-AUTH-DEPENDENCIES.md`](local-stack/W3-AUTH-DEPENDENCIES.md) | Traces the W3 setup’s Auth identity, permission, credential, TLS and EventBus dependencies. |
| [`local-stack/W3-BROKER-CHECK.md`](local-stack/W3-BROKER-CHECK.md) | Documents the disposable RabbitMQ-only W3 check and the prerequisites for later synthetic seeding. |
| [`local-stack/WORKER-CREDENTIAL-ROTATION.md`](local-stack/WORKER-CREDENTIAL-ROTATION.md) | Documents owner-controlled worker credential rotation, protected delivery and independent adoption checks. |

### Compose and Gateway configuration

| File | Purpose |
| --- | --- |
| [`local-stack/compose.yaml`](local-stack/compose.yaml) | Defines the standalone `zeka-m1` stack with PostgreSQL, RabbitMQ, Auth, AdminArea, Client and Gateway, plus its network, volumes and configuration mounts. |
| [`local-stack/compose.auth-https.yaml`](local-stack/compose.auth-https.yaml) | Adds the Auth HTTPS listener and mounts existing protected TLS material. |
| [`local-stack/compose.database-setup.yaml`](local-stack/compose.database-setup.yaml) | Defines the isolated PostgreSQL-only setup project used for database initialization. |
| [`local-stack/compose.w3-broker-check.yaml`](local-stack/compose.w3-broker-check.yaml) | Defines the disposable RabbitMQ-only W3 check. |
| [`local-stack/compose.worker-rotation.yaml`](local-stack/compose.worker-rotation.yaml) | Mounts external worker credential volumes for AdminArea and Client. |
| [`local-stack/ocelot.json`](local-stack/ocelot.json) | Defines the local Gateway route snapshot for browser sessions, eligible organisations and tenant access. |

### Configuration templates

| File | Purpose |
| --- | --- |
| [`local-stack/configuration/auth-https.template.json`](local-stack/configuration/auth-https.template.json) | Declares the expected Auth issuer, JWKS address, certificate names, listener settings and required TLS materials. |
| [`local-stack/configuration/database-settings.template.json`](local-stack/configuration/database-settings.template.json) | Describes the three service database settings, read-only delivery and authentication settings needed before runtime. |
| [`local-stack/configuration/worker-rotation-preparation/adminarea.preparation.json`](local-stack/configuration/worker-rotation-preparation/adminarea.preparation.json) | Records the proposed AdminArea worker authentication and tenant authorization settings and their required inputs. |
| [`local-stack/configuration/worker-rotation-preparation/auth.preparation.json`](local-stack/configuration/worker-rotation-preparation/auth.preparation.json) | Records the proposed Auth issuer, browser session and worker rotation settings and their required inputs. |
| [`local-stack/configuration/worker-rotation-preparation/client.preparation.json`](local-stack/configuration/worker-rotation-preparation/client.preparation.json) | Records the proposed Client worker authentication and tenant authorization settings and their required inputs. |
| [`local-stack/configuration/worker-rotation-preparation/gateway.preparation.json`](local-stack/configuration/worker-rotation-preparation/gateway.preparation.json) | Records the proposed Gateway authentication settings and their required inputs. |

### Scripts and shell settings

| File | Purpose |
| --- | --- |
| [`local-stack/Initialize-AuthHttpsConfiguration.ps1`](local-stack/Initialize-AuthHttpsConfiguration.ps1) | Generates Auth HTTPS environment settings and provenance records from approved private certificate, key and CA paths. |
| [`local-stack/Initialize-Databases.ps1`](local-stack/Initialize-Databases.ps1) | Checks the owner-local PostgreSQL resources, invokes database provisioning and writes restricted settings and sanitized receipts. |
| [`local-stack/Invoke-OwnerFirstAdmin.ps1`](local-stack/Invoke-OwnerFirstAdmin.ps1) | Checks the owner, source, helper image and database identity before running first-admin provisioning and recording its result. |
| [`local-stack/Invoke-WorkerCredentialRotation.ps1`](local-stack/Invoke-WorkerCredentialRotation.ps1) | Coordinates staged worker credential rotation through Auth HTTPS and guarded Docker volume delivery. Defaults to source-only checks. |
| [`local-stack/OwnerFirstAdminPreflightPrivileges.ps1`](local-stack/OwnerFirstAdminPreflightPrivileges.ps1) | Defines read-only SQL and assertions for the privileges required by first-admin provisioning. |
| [`local-stack/OwnerFirstAdminPreflightQueries.ps1`](local-stack/OwnerFirstAdminPreflightQueries.ps1) | Defines preflight transaction and query templates for bootstrap, privilege and RLS metadata checks. |
| [`local-stack/OwnerFirstAdminRlsMetadata.ps1`](local-stack/OwnerFirstAdminRlsMetadata.ps1) | Defines the RLS metadata query and checks the results against the first-admin manifest. |
| [`local-stack/SafePostgresDiagnostics.ps1`](local-stack/SafePostgresDiagnostics.ps1) | Sanitizes PostgreSQL SQLSTATE, messages, hints and positions, and parses legacy `psql` diagnostics. |
| [`local-stack/Test-Configuration.ps1`](local-stack/Test-Configuration.ps1) | Delegates configuration checks to the AuthManager integration-test script. |
| [`local-stack/Test-DatabaseBootstrap.ps1`](local-stack/Test-DatabaseBootstrap.ps1) | Delegates database bootstrap verification to the migration-tool test script. |
| [`local-stack/Test-OwnerSqlPostgreSql.ps1`](local-stack/Test-OwnerSqlPostgreSql.ps1) | Runs owner SQL and tool tests against disposable PostgreSQL and collects sanitized evidence. |
| [`local-stack/Test-W3AtomicWrite.ps1`](local-stack/Test-W3AtomicWrite.ps1) | Delegates the W3 atomic-write checks to the migration-tool test script. |
| [`local-stack/Test-W3NativeDiagnostics.ps1`](local-stack/Test-W3NativeDiagnostics.ps1) | Delegates the W3 native diagnostic checks to the migration-tool test script. |
| [`local-stack/W3-NativeDiagnostics.ps1`](local-stack/W3-NativeDiagnostics.ps1) | Provides redacted native-command diagnostics, Docker argument and mount checks, and atomic file writes. |
| [`local-stack/http-healthcheck.sh`](local-stack/http-healthcheck.sh) | Checks `GET /health/ready` on loopback port 8080 and succeeds on HTTP 200. |
| [`local-stack/.gitattributes`](local-stack/.gitattributes) | Keeps local-stack shell scripts on LF line endings. |
