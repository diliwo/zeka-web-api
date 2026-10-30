[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$FixtureDirectory = (Join-Path $env:LOCALAPPDATA 'Zeka\M1\bootstrap-fixtures'),
    [ValidatePattern('^[a-f0-9]{32}$')][string]$FixtureIdentity = [Guid]::NewGuid().ToString('N')
)
$ErrorActionPreference = 'Stop'
$stage = 'host-admission'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureId = $FixtureIdentity
$guardToken = 'zeka-m1-bootstrap-fixture-' + $fixtureId
$setupProject = 'zeka-m1-bootstrap-fixture-' + $fixtureId.Substring(0, 8)
$labelKey = 'com.diliwo.zeka.fixture-id'
$guardNetwork = $guardToken + '-guard-internal'
$guardVolume = $guardToken + '-guard-data'
$guardContainer = $guardToken + '-postgres'
$guardRunner = $guardToken + '-checks'
$createdContainers = @()
$createdNetworks = @()
$createdVolumes = @()
. (Join-Path $PSScriptRoot 'W3-NativeDiagnostics.ps1')
$diagnosticPath = Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) ('native-diagnostics-' + $fixtureId)
Initialize-W3Diagnostics -Directory $diagnosticPath -FixtureId $fixtureId -EngineId '0b62f5ec-15d9-4e4b-a450-817d52378eef' | Out-Null
function Fail([string]$Code) { Write-W3GuardRecord -Rule $Code -Operation $stage; throw [InvalidOperationException]::new($Code) }
function Native([string[]]$Argv, [int]$Milliseconds = 15000, [bool]$AllowFailure = $false, [string]$Exe = 'docker') {
    $name = [IO.Path]::GetFileNameWithoutExtension($Exe)
    $verb = $Argv[0]
    if ($name -eq 'docker' -and $verb -in @('volume','network','image','container')) { $verb += '.' + $Argv[1] }
    return Invoke-W3Native -Exe $Exe -ArgumentList $Argv -WorkingDirectory $repo -TimeoutMs $Milliseconds -Operation ($stage + '.' + $name + '.' + $verb) -AllowFailure:$AllowFailure
}
function ProtectedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Fail 'FIXTURE_PRIVATE_PATH_EXISTS' }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($owner)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($owner.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($identity)
        $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    $actual = Get-Acl -LiteralPath $Path
    $rules = $actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    $allowed = @($owner.Value, 'S-1-5-18', 'S-1-5-32-544')
    if (!$actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner.Value -or $rules.Count -ne 3 -or @($rules | Where-Object { $_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow }).Count -gt 0) { Fail 'FIXTURE_ACL_REJECTED' }
}
function SaveProtected([string]$Path, [byte[]]$Bytes) {
    if (Test-Path -LiteralPath $Path) { Fail 'FIXTURE_PRIVATE_OVERWRITE_REJECTED' }
    $temp = Join-Path (Split-Path -Parent $Path) ('.write-' + [Guid]::NewGuid().ToString('N'))
    $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temp, $Path)
    if ([IO.Path]::GetFileName($Path) -eq 'admin-password') { Register-W3Secret -Value ([Text.Encoding]::UTF8.GetString($Bytes)) }
}
function Absent([string]$Kind, [string]$Name) {
    $observed = Native @($Kind, 'inspect', $Name) 15000 $true
    if ($observed.Exit -eq 0) { Fail 'FIXTURE_RESOURCE_ALREADY_EXISTS' }
    if ($observed.Error -notmatch '(?i)(no such|not found)') { Fail 'FIXTURE_ABSENCE_UNCERTAIN' }
}
function Inspect([string]$Kind, [string]$Identity) {
    $records = (Native @($Kind, 'inspect', $Identity)).Out | ConvertFrom-Json
    if (@($records).Count -ne 1) { Fail 'FIXTURE_INSPECTION_AMBIGUOUS' }
    return $records[0]
}
function ResultSet([string]$Directory, [string]$Expected, [int]$Count) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter 'database-result-*.json' -File | Sort-Object Name)
    if ($files.Count -ne $Count) { Fail 'BOOTSTRAP_RESULT_COUNT_REJECTED' }
    $records = @(Get-Content -LiteralPath $files[-1].FullName -Raw | ConvertFrom-Json)
    if ($records.Count -ne 3 -or @($records.service | Sort-Object -Unique).Count -ne 3) { Fail 'BOOTSTRAP_SERVICE_SET_REJECTED' }
    foreach ($service in @('auth', 'adminarea', 'client')) {
        $r = @($records | Where-Object service -EQ $service)
        if ($r.Count -ne 1 -or $r[0].status -ne $Expected -or $r[0].wrongPasswordRejected -ne $true) { Fail 'BOOTSTRAP_RESULT_REJECTED' }
    }
    return $records
}
try {
    if (!$IsWindows) { Fail 'OWNER_WINDOWS_REQUIRED' }
    $context = (Native @('context', 'show')).Out.Trim()
    $engine = (Native @('info', '--format', '{{.ID}}')).Out.Trim()
    if ($context -ne 'default' -or $engine -ne '0b62f5ec-15d9-4e4b-a450-817d52378eef') { Fail 'DOCKER_TARGET_REJECTED' }
    $source = (Native @('rev-parse', 'HEAD') 15000 $false 'git').Out.Trim()
    if ($source -notmatch '^[0-9a-f]{40}$') { Fail 'SOURCE_COMMIT_REJECTED' }
    $image = 'zeka-m1-db-setup:' + $source.Substring(0, 12)
    $imageInfo = Inspect 'image' $image
    if ($imageInfo.Config.Labels.'org.opencontainers.image.revision' -ne $source) { Fail 'EXISTING_IMAGE_SOURCE_REJECTED' }
    $build = [IO.Path]::GetFullPath($BuildDirectory)
    $fixtureBase = [IO.Path]::GetFullPath($FixtureDirectory)
    $privateActual = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Zeka\M1\private'))
    foreach ($candidate in @($build, $fixtureBase)) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals($candidate, $repo) -or $candidate.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [StringComparer]::OrdinalIgnoreCase.Equals($candidate, $privateActual) -or $candidate.StartsWith($privateActual.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { Fail 'FIXTURE_PATH_BOUNDARY_REJECTED' }
    }
    if ($fixtureBase.StartsWith($build.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $build.StartsWith($fixtureBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [StringComparer]::OrdinalIgnoreCase.Equals($build, $fixtureBase)) { Fail 'FIXTURE_BUILD_PRIVATE_OVERLAP' }
    if (Test-Path -LiteralPath $fixtureBase) {
        if ((Get-Item -LiteralPath $fixtureBase).Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 'FIXTURE_REPARSE_REJECTED' }
    }
    $privateRoot = Join-Path $fixtureBase $fixtureId
    ProtectedDirectory $privateRoot
    $guardPrivate = Join-Path $privateRoot 'guard'
    ProtectedDirectory $guardPrivate
    $setupPrivate = Join-Path $privateRoot 'setup'
    ProtectedDirectory $setupPrivate
    $runRoot = Join-Path $build ('database-verification-' + $fixtureId)
    if (Test-Path -LiteralPath $runRoot) { Fail 'FIXTURE_RESULT_PATH_EXISTS' }
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    $guardResults = Join-Path $runRoot 'guard-results'
    [IO.Directory]::CreateDirectory($guardResults) | Out-Null
    $setupBuild = Join-Path $runRoot 'bootstrap'
    $admin = Join-Path $guardPrivate 'admin-password'
    $random = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    try { SaveProtected $admin ([Text.Encoding]::UTF8.GetBytes([Convert]::ToHexString($random))) } finally { [Array]::Clear($random, 0, $random.Length) }
    $marker = Join-Path $guardPrivate 'fixture-marker'
    SaveProtected $marker ([Text.Encoding]::UTF8.GetBytes($guardToken))
    foreach ($pair in @(@('container', $guardContainer), @('container', $guardRunner), @('network', $guardNetwork), @('volume', $guardVolume), @('container', ($setupProject + '-postgres-1')), @('network', ($setupProject + '_internal')), @('volume', ($setupProject + '_postgres_data')))) { Absent $pair[0] $pair[1] }
    $stage = 'guard-mount-admission'
    $adminMount = Get-W3MountSource -WindowsPath $admin
    $markerMount = Get-W3MountSource -WindowsPath $marker
    $resultMount = Get-W3MountSource -WindowsPath $guardResults
    $mountIntent = @{ fixtureId=$fixtureId; engineId=$engine; secretSource=$adminMount; destination='/run/secrets/admin-password'; type='bind'; readOnly=$true; credentialDelivery='protected file only'; createAttempts=0 }
    $mountIntent | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'mount-intent.json')
    $stage = 'guard-fixture-create'
    $networkId = (Native @('network', 'create', '--internal', '--label', ($labelKey + '=' + $fixtureId), $guardNetwork)).Out.Trim()
    $createdNetworks += @{ id = $networkId; name = $guardNetwork; fixture = $fixtureId }
    $null = Native @('volume', 'create', '--label', ($labelKey + '=' + $fixtureId), $guardVolume)
    $volume = Inspect 'volume' $guardVolume
    $createdVolumes += @{ name = $guardVolume; created = $volume.CreatedAt; fixture = $fixtureId }
    $createArgs = @('create', '--name', $guardContainer, '--network', $guardNetwork, '--network-alias', 'postgres-guard', '--label', ($labelKey + '=' + $fixtureId), '--log-driver', 'none', '--memory', '1g', '--env', 'POSTGRES_USER=postgres', '--env', 'POSTGRES_PASSWORD_FILE=/run/secrets/admin-password', '--env', 'POSTGRES_INITDB_ARGS=--auth-host=scram-sha-256', '--mount', ('type=bind,src=' + $adminMount.DaemonPath + ',dst=/run/secrets/admin-password,readonly'), '--mount', ('type=volume,src=' + $guardVolume + ',dst=/var/lib/postgresql/data'), 'postgres:17', '-c', 'log_statement=none', '-c', 'log_min_error_statement=panic')
    $decision = Assert-W3DockerArguments -ArgumentList $createArgs
    $mountIntent.guardDecision = $decision
    $mountIntent.createAttempts = 1
    $mountIntent | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'mount-intent.json')
    $guardId = (Native $createArgs).Out.Trim()
    $createdContainers += @{ id = $guardId; name = $guardContainer; fixture = $fixtureId }
    $created = Inspect 'container' $guardId
    $configuredMount = @($created.HostConfig.Mounts | Where-Object Target -EQ '/run/secrets/admin-password')
    if ($configuredMount.Count -ne 1 -or $configuredMount[0].Type -ne 'bind' -or $configuredMount[0].Source -ne $adminMount.DaemonPath -or !$configuredMount[0].ReadOnly) { Fail 'GUARD_EFFECTIVE_MOUNT_REJECTED' }
    $null = Native @('start', $guardId)
    $stage = 'guard-protected-mount-access'
    $null = Native @('exec', $guardId, '/bin/sh', '-c', 'test -f /run/secrets/admin-password && test -r /run/secrets/admin-password')
    $mounted = Inspect 'container' $guardId
    $effectiveMount = @($mounted.Mounts | Where-Object Destination -EQ '/run/secrets/admin-password')
    if ($effectiveMount.Count -ne 1 -or $effectiveMount[0].Type -ne 'bind' -or $effectiveMount[0].Source -ne $adminMount.DaemonPath -or $effectiveMount[0].RW) { Fail 'GUARD_EFFECTIVE_MOUNT_REJECTED' }
    @{fixtureId=$fixtureId;engineId=$engine;container=$guardId;source=$effectiveMount[0].Source;destination=$effectiveMount[0].Destination;type=$effectiveMount[0].Type;readOnly=(!$effectiveMount[0].RW);readable=$true;contentsDisclosed=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'protected-mount-result.json')
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $probe = Native @('exec', $guardId, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'postgres') 6000 $true
        if ($probe.Exit -eq 0) { $ready = $true; break }
        [Threading.Thread]::Sleep(500)
    }
    if (!$ready) { Fail 'GUARD_POSTGRES_NOT_READY' }
    $pg = Inspect 'container' $guardId
    $network = Inspect 'network' $networkId
    $ports = @($pg.HostConfig.PortBindings.PSObject.Properties)
    if (!$network.Internal -or $network.Labels.$labelKey -ne $fixtureId -or @($pg.NetworkSettings.Networks.PSObject.Properties).Count -ne 1 -or $ports.Count -ne 0 -or $pg.HostConfig.NetworkMode -eq 'host') { Fail 'GUARD_FIXTURE_ISOLATION_REJECTED' }
    $stage = 'guard-marker'
    $sql = "COMMENT ON DATABASE postgres IS '" + $guardToken + "';"
    $null = Native @('exec', $guardId, 'psql', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', $sql)
    $stage = 'guard-checks'
    $runnerId = (Native @('create', '--name', $guardRunner, '--network', $guardNetwork, '--label', ($labelKey + '=' + $fixtureId), '--env', ('ZEKA_M1_GUARD_FIXTURE=' + $guardToken), '--mount', ('type=bind,src=' + $adminMount.DaemonPath + ',dst=/run/secrets/admin-password,readonly'), '--mount', ('type=bind,src=' + $markerMount.DaemonPath + ',dst=/run/secrets/guard-fixture-marker,readonly'), '--mount', ('type=bind,src=' + $resultMount.DaemonPath + ',dst=/run/results'), $image, '--guard-checks')).Out.Trim()
    $createdContainers += @{ id = $runnerId; name = $guardRunner; fixture = $fixtureId }
    $guardRun = Native @('start', '--attach', $runnerId) 220000 $true
    $guardState = Inspect 'container' $runnerId
    $guardResultFile = Join-Path $guardResults 'guard-checks.json'
    if ($guardRun.Exit -ne 0 -or $guardState.State.Running -or $guardState.State.ExitCode -ne 0 -or !(Test-Path -LiteralPath $guardResultFile)) { Fail 'GUARD_CHECKS_FAILED' }
    $guardResult = Get-Content -LiteralPath $guardResultFile -Raw | ConvertFrom-Json
    if ($guardResult.schema -ne 'zeka-m1-bootstrap-guards/v1' -or $guardResult.status -ne 'passed' -or $guardResult.passed -ne 9 -or $guardResult.failureCode -or @($guardResult.cases).Count -ne 9 -or @($guardResult.cases | Where-Object Status -NE 'passed').Count -gt 0) { Fail 'GUARD_RESULT_REJECTED' }
    $stage = 'bootstrap-first-invocation'
    $wrapper = Join-Path $PSScriptRoot 'Initialize-Databases.ps1'
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $argv = @('-NoProfile', '-File', $wrapper, '-BuildDirectory', $setupBuild, '-PrivateDirectory', $setupPrivate, '-ProjectName', $setupProject, '-Fixture')
    $invoke = Native $argv 1200000 $true $pwsh
    [IO.File]::WriteAllText((Join-Path $runRoot ($stage + '.log')), $invoke.Out, [Text.UTF8Encoding]::new($false))
    if ($invoke.Exit -ne 0) { Fail 'BOOTSTRAP_INVOCATION_FAILED' }
    $first = ResultSet $setupBuild 'INITIALIZED' 1
    $progressFile = Join-Path $setupPrivate 'database\setup-progress.json'
    $progress = Get-Content -LiteralPath $progressFile -Raw | ConvertFrom-Json
    if ($progress.project -ne $setupProject -or $progress.sourceCommit -ne $source -or !$progress.clusterId -or !$progress.containerId) { Fail 'BOOTSTRAP_PROGRESS_REJECTED' }
    $setupId = $progress.initializationId
    $setupPg = Inspect 'container' $progress.containerId
    $setupNetwork = Inspect 'network' ($setupProject + '_internal')
    $setupVolume = Inspect 'volume' ($setupProject + '_postgres_data')
    if ($setupPg.Config.Labels.'com.diliwo.zeka.initialization-id' -ne $setupId -or $setupNetwork.Labels.'com.diliwo.zeka.initialization-id' -ne $setupId -or $setupVolume.Labels.'com.diliwo.zeka.initialization-id' -ne $setupId) { Fail 'BOOTSTRAP_FIXTURE_IDENTITY_REJECTED' }
    $createdContainers += @{ id = $setupPg.Id; name = ($setupProject + '-postgres-1'); project = $setupProject; initialization = $setupId }
    $createdNetworks += @{ id = $setupNetwork.Id; name = $setupNetwork.Name; project = $setupProject; initialization = $setupId }
    $createdVolumes += @{ name = $setupVolume.Name; created = $setupVolume.CreatedAt; project = $setupProject; initialization = $setupId }
    $privateFingerprints = @{}
    foreach ($relative in @('postgres\admin-password', 'auth\migrator-password', 'auth\runtime-password', 'auth\database.LocalStack.json', 'adminarea\migrator-password', 'adminarea\runtime-password', 'adminarea\database.LocalStack.json', 'client\migrator-password', 'client\runtime-password', 'client\database.LocalStack.json')) {
        $privateFingerprints[$relative] = (Get-FileHash -LiteralPath (Join-Path $setupPrivate $relative) -Algorithm SHA256).Hash
    }
    $stage = 'bootstrap-second-invocation'
    $invoke = Native $argv 1200000 $true $pwsh
    [IO.File]::WriteAllText((Join-Path $runRoot ($stage + '.log')), $invoke.Out, [Text.UTF8Encoding]::new($false))
    if ($invoke.Exit -ne 0) { Fail 'BOOTSTRAP_INVOCATION_FAILED' }
    $second = ResultSet $setupBuild 'ALREADY_CURRENT' 2
    foreach ($relative in $privateFingerprints.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $setupPrivate $relative) -Algorithm SHA256).Hash -ne $privateFingerprints[$relative]) { Fail 'CREDENTIAL_OR_CONFIGURATION_CHANGED' }
    }
    $privateFingerprints.Clear()
    foreach ($before in $first) {
        $after = @($second | Where-Object service -EQ $before.service)[0]
        if ($after.schemaSha256 -ne $before.schemaSha256 -or $after.historySha256 -ne $before.historySha256 -or $after.clusterId -ne $before.clusterId -or $after.initializationId -ne $before.initializationId -or $after.sourceCommit -ne $source) { Fail 'REPEAT_DATABASE_SNAPSHOT_CHANGED' }
    }
    $stage = 'verified-fixture-preservation'
    @{ fixtureId=$fixtureId; containers=$createdContainers; volumes=$createdVolumes; networks=$createdNetworks; automaticCleanup=$false } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot 'preserved-resources.json')
    $summary = @{ status = 'PASS'; sourceCommit = $source; engineId = $engine; fixtureId = $fixtureId; guardChecks = 'PASS'; firstInvocation = 'INITIALIZED'; secondInvocation = 'ALREADY_CURRENT'; services = 3; credentialFilesUnchanged = $true; schemaHistoryUnchanged = $true; dockerFixtureCleanup = 'not attempted; all fixture resources preserved'; protectedFixtureDirectory = $privateRoot }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'verification-result.json')
    Write-Output ($summary | ConvertTo-Json -Compress)
} catch {
    $code = 'GUARD_OR_OPERATION_REJECTED'
    $allowedGuardCodes = @('MOUNT_SOURCE_MISSING', 'MOUNT_SOURCE_HANDLE_REJECTED', 'MOUNT_SOURCE_PHYSICAL_PATH_UNAVAILABLE', 'MOUNT_SOURCE_LOCAL_DRIVE_REJECTED', 'MOUNT_SOURCE_TRANSLATION_REJECTED', 'MOUNT_SOURCE_WSL_UNREADABLE', 'DIAGNOSTICS_INSIDE_CHECKOUT_REJECTED', 'DIAGNOSTIC_IDENTITY_REJECTED', 'DIAGNOSTIC_REPARSE_REJECTED', 'DIAGNOSTICS_NOT_INITIALIZED', 'NATIVE_TIMEOUT_INPUT_REJECTED', 'GUARD_EFFECTIVE_MOUNT_REJECTED', 'W3_ARGUMENT_SECURITY_REJECTED', 'OPERATION_TIMEOUT_REQUIRES_INSPECTION', 'BOOTSTRAP_INVOCATION_FAILED', 'BOOTSTRAP_FIXTURE_IDENTITY_REJECTED', 'BOOTSTRAP_PROGRESS_REJECTED', 'BOOTSTRAP_RESULT_COUNT_REJECTED', 'BOOTSTRAP_RESULT_REJECTED', 'BOOTSTRAP_SERVICE_SET_REJECTED', 'CLEANUP_CONTAINER_IDENTITY_REJECTED', 'CLEANUP_FIXTURE_LABEL_REJECTED', 'CLEANUP_INITIALIZATION_LABEL_REJECTED', 'CLEANUP_NETWORK_IDENTITY_REJECTED', 'CLEANUP_VOLUME_IDENTITY_REJECTED', 'CREDENTIAL_OR_CONFIGURATION_CHANGED', 'DOCKER_TARGET_REJECTED', 'EXISTING_IMAGE_SOURCE_REJECTED', 'FIXTURE_ABSENCE_UNCERTAIN', 'FIXTURE_ACL_REJECTED', 'FIXTURE_BUILD_PRIVATE_OVERLAP', 'FIXTURE_INSPECTION_AMBIGUOUS', 'FIXTURE_PATH_BOUNDARY_REJECTED', 'FIXTURE_PRIVATE_OVERWRITE_REJECTED', 'FIXTURE_PRIVATE_PATH_EXISTS', 'FIXTURE_REPARSE_REJECTED', 'FIXTURE_RESOURCE_ALREADY_EXISTS', 'FIXTURE_RESULT_PATH_EXISTS', 'GUARD_CHECKS_FAILED', 'GUARD_FIXTURE_ISOLATION_REJECTED', 'GUARD_POSTGRES_NOT_READY', 'GUARD_RESULT_REJECTED', 'NATIVE_OPERATION_REJECTED', 'OPERATION_TIMEOUT_FIXTURES_PRESERVED', 'OWNER_WINDOWS_REQUIRED', 'PROCESS_START_FAILED', 'REPEAT_DATABASE_SNAPSHOT_CHANGED', 'SOURCE_COMMIT_REJECTED')
    if ($_.Exception -is [InvalidOperationException] -and $_.Exception.Message -in $allowedGuardCodes) { $code = $_.Exception.Message }
    $failure = @{ status = 'BLOCKED'; stage = $stage; code = $code; fixtureId = $fixtureId; fixturesPreserved = $true; diagnosticDirectory = $diagnosticPath }
    if ($runRoot -and (Test-Path -LiteralPath $runRoot)) { $failure | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'verification-blocker.json') }
    Write-Output ($failure | ConvertTo-Json -Compress)
    exit 1
}
