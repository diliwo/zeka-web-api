# Local M1 owner first-admin command

This private C1 adapter is admitted only for the approved Windows owner and isolated `zeka-m1` initialization. It invokes the shared `AuthManager.Application.Provisioning.OrganisationProvisioningService`; the adapter does not create organisations or memberships itself. There is no public HTTP endpoint.

C1 uses explicitly owner-attested, already-confirmed Identity authority. The fixed approved owner email is not a configurable public-registration input. A known existing identity must already be active and confirmed; its password is never reset. The operator audit states **operator-provisioned, no agreements**. No email, verification credential, grant, agreement catalog or acceptance ledger is used. Self-service remains disabled pending C2.

## Before execution

Use the clean `codex/m1-c1-integration` checkout at `D:\Workspace\zeka-m1-w3-synthetic-seeding`, normal Windows SDK8.0.129 and the approved default Rancher Docker engine. Keep the existing M1 database bootstrap state and its source revision unchanged. The wrapper checks its recorded initialization, resource IDs/labels, private ACLs and owner SID. It uses only the Auth runtime database configuration and the protected first-admin password; no migrator/admin/JWT/TLS/SMTP secret is delivered.

Publish from an immutable candidate into a new directory outside Git and the M1 private directory:

```powershell
$commit = (git rev-parse HEAD).Trim()
$publish = "D:\Workspace\CodexDiagnostics\owner-$($commit.Substring(0,12))\publish"
dotnet publish Tools/Zeka.AuthOwnerProvision/Zeka.AuthOwnerProvision.csproj -c Release -r linux-x64 --self-contained false -p:ZekaSourceSha=$commit -o $publish
$binary = Join-Path $publish 'zeka-auth-owner-provision.dll'
$binarySha = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
$tag = "zeka/m1-owner-provision:$($commit.Substring(0,12))"
$context = "$publish-context"
$receipt = & ./Tools/Zeka.AuthOwnerProvision/Prepare-OwnerHelperBuildContext.ps1 -PublishDirectory $publish -ContextDirectory $context -SourceCommit $commit -BinarySha256 $binarySha | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $receipt.status -ne 'PREPARED') { throw 'OWNER_HELPER_CONTEXT_REJECTED' }
. ./Tools/Zeka.AuthOwnerProvision/Prepare-OwnerHelperBuildContext.ps1 -DefinitionsOnly
Assert-OwnerHelperPreparedContext $context $receipt
docker build -f (Join-Path $context 'Dockerfile') --build-arg SOURCE_COMMIT=$commit --build-arg BINARY_SHA256=$binarySha -t $tag $context
$image = (docker image inspect --format '{{.Id}}' $tag).Trim()
```

Capture the publish/binary/image/source receipt and verify the source is still clean. The preparer rejects any publish output outside its exact 38-file inventory, copies only 31 runtime files, and excludes the seven known symbol/apphost outputs. The actual temporary context contains its own default-deny .dockerignore and the reviewed explicit-COPY Dockerfile; do not build directly from the publish directory. Revalidate the context immediately before building. Private configuration never enters either directory.

If the executing parent contains database credential environment variables, use the existing process-environment-copy boundary for the wrapper child. Remove only names matching `^(?i:PGPASSWORD|POSTGRES_PASSWORD|ConnectionStrings__.+)$` from that child's environment; preserve the parent and normal Windows SDK. This is secret isolation, not a guard override: a create invocation that still inherits those names must fail closed. Never print their values.

## Owner preparation and one attempt

The logical private root is `C:\Users\herve\AppData\Local\Zeka\M1\private`; the wrapper resolves packaged-app physical storage through the existing metadata helper. Prepare once, then execute with the same admitted profile:

```powershell
$private = 'C:\Users\herve\AppData\Local\Zeka\M1\private'
& ./Deployments/local-stack/Invoke-OwnerFirstAdmin.ps1 -OwnerAttestedIdentity -PrepareOnly -PrivateDirectory $private -SourceCommit $commit
& ./Deployments/local-stack/Invoke-OwnerFirstAdmin.ps1 -OwnerAttestedIdentity -PrivateDirectory $private -SourceCommit $commit -BinarySha256 $binarySha -ToolBinaryPath $binary -ToolImage $image
```

`-ExistingSubjectId` is an explicit preparation-only input when the legitimate confirmed owner subject is already known. Unknown, unconfirmed, suspended or mismatched existing accounts are refused, not silently adopted or confirmed.

Preparation saves the stable subject/idempotency intent and fresh password with restricted permissions before database work. Repeats preserve saved credential bytes. Credentials travel through bounded private stdin, not command arguments, container environment, mounts or logs. A helper is newly created, non-root, read-only except its bounded `/tmp`, and retained. No existing container/volume is restarted or removed.

The service commits identity, organisation, fixed Owner membership, audit, outbox intent and idempotency outcome in one Auth transaction. Replay returns original safe IDs without credential changes. Completed idempotency records and the protected profile are M1 initialization state: do not independently delete them. Tests distinguish replay after retention expiry while the record remains from unsupported deletion/reset.

Any nonzero/uncertain attempt retains an inspection marker and evidence; the wrapper refuses an automatic retry. Do not clear that marker without the required inspection. A PASS receipt is scoped provisioning evidence, not proof of TLS, sign-in, worker issuance, EventBus delivery or staff projection. Outbox enqueue is not delivery; organisation timezone remains unset until separately configured.

## Verification

Normal `dotnet test` covers the application, CLI protocol and SQLite persistence suites. `OperatorBoundary/Test-OwnerAdmission.ps1 -ReportFile <new-file>` exercises synthetic admission/receipt facts without reading M1 private files or running Docker. SQLite and synthetic facts do not prove PostgreSQL RLS/runtime-role behavior or actual Windows owner execution. Required exact-head Linux CI and Chief's independent review remain separate gates. BuildContext/Test-OwnerHelperBuildContext.ps1 exercises synthetic file-boundary controls. BuildContext/Test-OwnerImageContainment.py exercises actual local images, a synthetic forbidden-file negative, filesystem/layer inspection and network-none startup refusals using published binaries. Neither test provisions an owner or proves live database authority. C1 acceptance requires its security and correctness/runtime matrix; the full existing Sonar gate, including 80% new-code coverage, still governs merging into develop.
