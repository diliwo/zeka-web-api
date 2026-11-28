# Local MVP-02 beneficiary lookup

The fixture runs the Angular frontend, HTTPS Gateway, Auth and Client APIs, PostgreSQL 17, and RabbitMQ on the owner's Windows Rancher Desktop engine. It uses the owner-managed Mailpit SMTP endpoint through its existing private network. Its database, broker, credentials, and CA belong to this fixture only.

## Resume fixture 42626189

Run from the backend task worktree in PowerShell 7.4 or later. If Docker or the workstation stopped, first start Rancher Desktop and check that the owner-managed Mailpit is healthy.

```powershell
docker --context default start zeka-mvp02-42626189-postgres-1 zeka-mvp02-42626189-rabbitmq-1
pwsh -NoProfile -File Deployments/local-stack/mvp02/Start-Mvp02Applications.ps1 -FixtureId 42626189
```

The launcher checks the owned bridge and image identities. It completes a strict private Auth JWKS request before checking Auth readiness, then checks Client and Gateway. Complete the JWKS request again after an Auth restart before retrying sign-in.

Open `https://app.mvp02.zeka-m1.test:24446` in the admitted fixture browser attached to `zeka-mvp02-42626189_edge`. Its public CA is `D:\Workspace\ZekaLocal\zeka-mvp02-42626189\tls\ca.pem`. The browser must resolve the exact HTTPS name and trust that CA. Windows host ingress binds only to `127.0.0.1:24446`. A health status proves readiness only for the component answering it.

## Optional Windows browser access

The automated witness uses a disposable browser with the fixture CA. To open the same application in a regular Windows browser, the owner can separately allow the exact loopback name and constrained development CA for this Windows account.

1. In an elevated editor, inspect `%SystemRoot%\System32\drivers\etc\hosts`. If `app.mvp02.zeka-m1.test` has a conflicting mapping, stop. Add this single line only when the name has no existing entry.

   ```text
   127.0.0.1 app.mvp02.zeka-m1.test # Zeka MVP-02 42626189
   ```

   Confirm every address returned by `[Net.Dns]::GetHostAddresses('app.mvp02.zeka-m1.test')` is `127.0.0.1`. Leave other host entries intact.

2. In ordinary PowerShell 7.4, verify the public CA and add it to **CurrentUser** Root only if it is absent. The fingerprint below identifies this fixture's actual CA. Use the fingerprint reported by `New-Mvp02Tls.ps1` for a fresh fixture.

   ```powershell
   $expectedCa = 'A30D922AEB82530BDCCC5EE68B169104B227B83E7C507B50CDBAA6D720844CD9'
   $publicCa = 'D:\Workspace\ZekaLocal\zeka-mvp02-42626189\tls\ca.pem'
   $ca = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem([IO.File]::ReadAllText($publicCa))
   $hashName = [Security.Cryptography.HashAlgorithmName]::SHA256
   if ($ca.GetCertHashString($hashName) -cne $expectedCa -or $ca.NotAfter -le (Get-Date)) { throw 'Fixture CA identity rejected' }
   $store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'CurrentUser')
   $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
   try {
       $existing = @($store.Certificates | Where-Object { $_.GetCertHashString($hashName) -ceq $expectedCa })
       if ($existing.Count -eq 0) { $store.Add($ca); Write-Output 'Added fixture CA' }
       else { Write-Output 'Fixture CA already present' }
   } finally { $store.Dispose(); $ca.Dispose() }
   ```

3. Reopen the browser and visit `https://app.mvp02.zeka-m1.test:24446`. A certificate error, unexpected hostname, or non-loopback resolution stops the test. Keep browser certificate validation enabled.

**Rollback.** If you added the hosts entry, remove only the exact marked line above. If PowerShell reported `Added fixture CA`, remove only the certificate with the displayed SHA-256 from **CurrentUser** Root. Keep the signed leaf and other trust entries in place. The owner can locate the exact public certificate for removal with the following command.

```powershell
Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -ceq $expectedCa } | Select-Object Subject, Thumbprint, NotAfter
```

Use `certmgr.msc` and compare the exact thumbprint before removing that certificate in **Trusted Root Certification Authorities → Certificates**. Close and reopen the browser after removal.

## Local witness on 28 November 2026

The disposable Chromium witness passed 26 checks against real HTTPS Gateway, Auth, Client, and PostgreSQL. It covered synthetic sign-in and authorised lookup and identity reload, cross-organisation and assignment denial, revoked membership, and the actual Auth-only outage. The same Auth container recovered after a strict JWKS publication and a readiness check. The test records contain no credentials, tokens or private keys.
## Build a fresh synthetic fixture

1. Prepare clean task builds for the backend and frontend. Restore Auth, Client, Gateway and `Tools/Zeka.LocalStack.DatabaseSetup/Zeka.LocalStack.DatabaseSetup.csproj` with the repository NuGet configuration and `dotnet restore --locked-mode`. Use the approved GitHub Packages credential delivery or the verified read-only package cache. Publish the four projects in Release with `--no-restore` into separate directories outside the checkout. Bind the setup runner to the current Git SHA with `-p:ZekaSourceSha=<source SHA>`. Use the frontend repository's locked dependency and production build procedures.
2. Choose a fresh eight-character lowercase hexadecimal fixture ID. This recipe uses internal `10.248.52.0/28` and edge `10.248.53.0/28`. It stops on overlap with current Windows, Docker or Rancher routes. The existing fixture occupies these ranges while present. Never remove another project's resources to free them.

   ```powershell
   $fixture = '<fresh eight-character hexadecimal identifier>'
   $dir = 'Deployments/local-stack/mvp02'
   pwsh -NoProfile -File "$dir/Start-Mvp02Infrastructure.ps1" -FixtureId $fixture
   ```

3. Build the setup image using [`Tools/Zeka.LocalStack.DatabaseSetup/Dockerfile`](../../../Tools/Zeka.LocalStack.DatabaseSetup/Dockerfile) and a context containing only its `published/` Release output. Set `org.opencontainers.image.revision` to the published source SHA and `com.diliwo.zeka.mvp02` to the fixture ID. Pass the resulting `sha256:` image ID to the unchanged database setup runner wrapper. It installs and verifies the existing Auth and Client database contracts on the owned cluster.

   ```powershell
   pwsh -NoProfile -File "$dir/Initialize-Mvp02Databases.ps1" -FixtureId $fixture -SetupImage $setupImageId -SetupSource $sourceSha
   pwsh -NoProfile -File "$dir/New-Mvp02Tls.ps1" -FixtureId $fixture
   pwsh -NoProfile -File "$dir/Configure-Mvp02Applications.ps1" -FixtureId $fixture
   ```

4. For each API, prepare an independent Docker context with its Release output under `app/` and the fixture's **public** `ca.pem` copied as `mvp02-ca.crt`. Build with `Api.Dockerfile`, label `com.diliwo.zeka.mvp02=$fixture`, and tag `zeka-mvp02-{auth,client,gateway}-exact:<fixture ID>`. The API image installs only the public CA. Private settings and leaf keys come from the read-only mounts declared in Compose. Keep the protected runtime root out of build contexts.
5. Build `zeka-mvp02-frontend:<fixture ID>` with `Frontend.Dockerfile`, the same fixture label, production Angular static files under `html/`, and both nginx `.conf` files at the context root. Omit generated environment configuration files from the copied build. The runtime config mount supplies `/assets/config/config.json`; create an empty `{}` at that path in the context. The pinned nginx base needs to be available locally. Start the apps with `Start-Mvp02Applications.ps1 -FixtureId $fixture`.

## Seed and verify

Seed a fresh fixture after the launcher reports strict HTTPS readiness.

```powershell
$stateRoot = "D:\Workspace\ZekaLocal\zeka-mvp02-$fixture"
pwsh -NoLogo -NoProfile -File "$dir/Seed-Mvp02Beneficiaries.ps1" -StateRoot $stateRoot -Enroll
```

The helper registers fictional accounts via the actual Gateway, deliberately confirms them through Mailpit, signs in, and creates two organisations. It uses only the matching restricted Auth and Client roles on the owned PostgreSQL fixture. Synthetic account and binding files stay in the protected runtime directory.

The live witness used project-bound equivalents for enrollment and seeding. The portable helper passed syntax and real read-only fixture preflight.

`-PreflightOnly` verifies the current Docker and database identity without changing data. On the existing fixture `42626189`, use `-DatabaseDirectory database-attempt-2` for read-only preflight against the admitted historical database delivery. Run enrollment only on a fresh fixture.

In Angular, select the appropriate organisation and search `Mvp02lookup`. Alpha owner lookup returns 20 suggestions and indicates more. Beta returns one eligible dossier. The assigned-only synthetic user sees one result, `Mvp02lookup Alpha00`. Open the result and reload its read-only identity route. Other organisations' dossiers must stay inaccessible through direct navigation.
