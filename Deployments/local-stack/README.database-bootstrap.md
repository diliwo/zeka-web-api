# M1 three-service database bootstrap

Run this checkpoint on the owner's Windows workstation with the inherited .NET SDK 8.0.129 and the approved default Docker engine. It initializes the isolated Auth, AdminArea and Client databases, in that order, without starting application consumers.

## Invocation and repeatability

Commit and record the reviewed source candidate before invoking the wrapper. The wrapper records the current full Git HEAD, template hash, role-SQL hashes, Docker engine ID and initialization ID in protected progress. A later invocation must use the same source candidate and retained PostgreSQL resources; source drift is a stop condition, not an automatic upgrade.

From the W3 worktree, invoke:

~~~powershell
Set-Location D:\Workspace\zeka-m1-w3-synthetic-seeding
$env:ZEKA_M1_PRIVATE_DIR = 'C:\Users\herve\AppData\Local\Zeka\M1\private'
.\Deployments\local-stack\Initialize-Databases.ps1 -BuildDirectory 'D:\Workspace\zeka-m1-w3-database-build'
~~~

The build directory must be outside the checkout and outside the private configuration root, with no ancestor overlap with that root. It contains the published helper and sanitized per-service request/result records. Its path is not a secret.

After the entire first invocation succeeds, run the same command once more against the same candidate, private directory and retained resources. The second invocation must report ALREADY_CURRENT for every service, preserving credentials and the recorded schema/history snapshots. A first invocation or second invocation that reports BLOCKED is a failed gate: stop and inspect the retained evidence; do not repeatedly invoke it, rotate passwords or recreate resources to obtain a pass.

The final DATABASE_SETUP_VERIFIED; APPLICATION_CONFIGURATION_NOT_READY line proves only the database checkpoint. It does not prove HTTPS, first-administrator provisioning, worker authentication, application admission or synthetic-event projection.

## Protected configuration and initialization identity

The private root must already have its owner/SYSTEM/Administrators-only ACL with inheritance disabled. Existing administrator, Auth key and RabbitMQ files are preserved. The normal path requires the existing postgres/admin-password and never replaces it.

For each service, the wrapper saves separate migrator-password and runtime-password files in that service's private subdirectory. Files are written through a protected temporary file, flushed to disk and atomically moved before the saved values can be applied. Established files are reused and validated, never overwritten. If existing PostgreSQL resources make prior role creation possible, a missing credential file is a blocker.

The private database/setup-progress.json records the unique initialization ID and progress. The helper correlates it with database/role metadata, source identity and retained cluster identity. Pre-existing foreign or unmarked roles are not adopted. A missing retained container or volume after a cluster/container identity has been recorded is rejected before startup.

Each service also receives its own private database.LocalStack.json component:

- auth/database.LocalStack.json uses ConnectionStrings:Default.
- adminarea/database.LocalStack.json uses ConnectionStrings:ClientApiConnection.
- client/database.LocalStack.json uses ConnectionStrings:ClientApiConnection.

These files contain only that service's database settings. They are separate components, not complete application configuration. The tracked configuration/database-settings.template.json documents the nonsecret mappings and lists the still-required Auth inputs; applicationReady remains false.

The helper gets the administrator password, current service's two passwords, current service's database component and role SQL through separate read-only file mounts. Password values do not enter command arguments, environment variables, Git or build contexts. The migrator receives its credential through stdin. Do not print or attach private files.

## Resource and failure boundaries

compose.database-setup.yaml creates a persistent PostgreSQL 17 volume and an internal bridge network under zeka-m1. No database port is published and host networking is rejected. Admission checks require the expected project/initialization labels, exact network membership and expected writable PostgreSQL data-volume mount. The database network does not attach to a shared external network.

The wrapper validates the approved default Docker context and engine ID. It does not restart or reuse historical resources. Fixture mode requires a separate guarded fixture project name and private root; it must not target the actual zeka-m1 project or owner private root.

Individual host operation limits remain active: resource observations use 15 seconds; ordinary commands default to 120 seconds; publishing uses 180 seconds; the image build uses 120 seconds; PostgreSQL startup uses 90 seconds with a 60-second Compose readiness limit; each service helper uses 330 seconds. These limits are not a task-level time cap.

On uncertain timeout, protected progress is marked requiresInspection where available. Database startup/service-operation failures likewise prevent an automatic retry. A killed Docker client does not establish that its container operation completed or stopped; inspect the retained resource state before any separately authorized recovery.

The wrapper reports only allowlisted guard codes and safe result fields. Preserve result files and progress for review. Do not remove a failure marker, erase evidence, reset a volume, migrate another service or begin TLS/worker/seeding work as a way to continue this checkpoint.
