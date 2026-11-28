#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}$')][string]$FixtureId,
    [Parameter(Mandatory)][ValidatePattern('^sha256:[a-f0-9]{64}$')][string]$SetupImage,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SetupSource
)

$ErrorActionPreference = 'Stop'
$project = "zeka-mvp02-$FixtureId"
$engine = '0b62f5ec-15d9-4e4b-a450-817d52378eef'
$privateRoot = "D:\Workspace\ZekaLocal\$project"
$privateDatabase = Join-Path $privateRoot 'database'
$statePath = Join-Path $privateRoot 'infrastructure.json'
$deployment = Split-Path $PSScriptRoot -Parent
$repository = [IO.Path]::GetFullPath((Join-Path $deployment '..\..'))

function Get-DockerMetadata([string[]]$Arguments) {
    $output = @(& docker --context default @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ZEK52_DATABASE_DOCKER_METADATA_FAILED (native $LASTEXITCODE)" }
    return $output
}

function New-Password {
    [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}

function Write-FixtureFile([string]$Path, [string]$Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose(); [Array]::Clear($bytes) }
}

function Get-SafeResultCode($Value) {
    if ([string]$Value -cmatch '^[A-Za-z][A-Za-z0-9_-]{1,64}$') { return [string]$Value }
    return 'UNKNOWN'
}

$owner = [Security.Principal.WindowsIdentity]::GetCurrent()
$privateAcl = Get-Acl -LiteralPath $privateRoot
if ($owner.Name -ine 'DILI_LP\herve' -or !$privateAcl.AreAccessRulesProtected -or
    $privateAcl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner.User -or
    ((Get-Item -LiteralPath $privateRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ZEK52_DATABASE_PRIVATE_ROOT_REJECTED'
}
if (Test-Path -LiteralPath $privateDatabase) { throw 'ZEK52_DATABASE_ATTEMPT_ALREADY_EXISTS' }
$head = (git --no-optional-locks -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $SetupSource) { throw 'ZEK52_DATABASE_SOURCE_REJECTED' }

$diagnosticSource = Join-Path $deployment 'W3-NativeDiagnostics.ps1'
$safeDiagnosticSource = Join-Path $deployment 'SafePostgresDiagnostics.ps1'
$bootstrapRoot = [IO.Path]::GetFullPath((Join-Path $deployment '..\database'))
$paths = @($statePath, $diagnosticSource, $safeDiagnosticSource,
    (Join-Path $bootstrapRoot 'bootstrap-auth-roles.sql'), (Join-Path $bootstrapRoot 'bootstrap-client-roles.sql'))
foreach ($path in $paths) {
    $scan = @(sonar analyze secrets $path 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'ZEK52_DATABASE_PRE_READ_SCAN_REJECTED' }
}
$state = ConvertFrom-Json -AsHashtable -InputObject (Get-Content -LiteralPath $statePath -Raw)
if ($state.project -cne $project -or $state.fixtureId -cne $FixtureId -or $state.engineId -cne $engine -or
    $state.internalNetwork -cne ($project + '_internal') -or $state.initialisationId -cnotmatch '^[a-f0-9-]{36}$') {
    throw 'ZEK52_DATABASE_FIXTURE_RECEIPT_REJECTED'
}
if ((docker context show) -cne 'default' -or $LASTEXITCODE -ne 0 -or
    (Get-DockerMetadata @('info', '--format', '{{.ID}}')) -cne $engine) {
    throw 'ZEK52_DATABASE_ENGINE_REJECTED'
}
$network = Get-DockerMetadata @('network', 'inspect', $state.internalNetwork, '--format',
    '{{.Id}}|{{.Internal}}|{{index .Labels "com.diliwo.zeka.mvp02"}}')
if ($network -cne "$($state.internalNetworkId)|true|$FixtureId") { throw 'ZEK52_DATABASE_NETWORK_REJECTED' }
$postgres = Get-DockerMetadata @('container', 'inspect', ($project + '-postgres-1'), '--format',
    '{{index .Config.Labels "com.docker.compose.project"}}|{{.State.Health.Status}}|{{len .HostConfig.PortBindings}}|{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}')
if ($postgres -cne "$project|healthy|0|$($state.internalNetwork)") { throw 'ZEK52_DATABASE_POSTGRES_REJECTED' }
$image = Get-DockerMetadata @('image', 'inspect', $SetupImage, '--format',
    '{{.Id}}|{{index .Config.Labels "org.opencontainers.image.revision"}}|{{index .Config.Labels "com.diliwo.zeka.mvp02"}}|{{.Config.User}}|{{json .Config.Entrypoint}}')
if ($image -cne ('{0}|{1}|{2}|app|["dotnet","zeka-local-db-setup.dll"]' -f $SetupImage, $SetupSource, $FixtureId)) {
    throw 'ZEK52_DATABASE_IMAGE_REJECTED'
}

. $diagnosticSource
[IO.Directory]::CreateDirectory($privateDatabase) | Out-Null
Initialize-W3Diagnostics -Directory (Join-Path $privateDatabase 'diagnostics') -FixtureId $project -EngineId $engine | Out-Null
$adminMount = Get-W3MountSource -WindowsPath (Join-Path $privateRoot 'postgres-admin')
$clusterId = $null
foreach ($service in @('auth', 'client')) {
    $serviceRoot = Join-Path $privateDatabase $service
    $results = Join-Path $serviceRoot 'results'
    [IO.Directory]::CreateDirectory($results) | Out-Null
    $request = @{
        # This is the unchanged runner's schema/role namespace, separate from the Docker project.
        Project = 'zeka-m1'; Service = $service; InitializationId = $state.initialisationId
        SourceCommit = $SetupSource; Completed = $false; ClusterId = $clusterId
        SchemaSha256 = $null; HistorySha256 = $null
    }
    $requestFile = Join-Path $serviceRoot 'request.json'
    $migratorFile = Join-Path $serviceRoot 'migrator-password'
    $runtimeFile = Join-Path $serviceRoot 'runtime-password'
    $runtimeSettings = Join-Path $serviceRoot 'database.LocalStack.json'
    $key = if ($service -eq 'auth') { 'Default' } else { 'ClientApiConnection' }
    $runtimeValue = New-Password
    try {
        Write-FixtureFile $requestFile ($request | ConvertTo-Json -Compress)
        Write-FixtureFile $migratorFile (New-Password)
        Write-FixtureFile $runtimeFile $runtimeValue
        $connection = "Host=postgres;Port=5432;Database=zeka_m1_$service;Username=zeka_${service}_runtime;Password=$runtimeValue;Pooling=false;Timeout=5;Command Timeout=30;SSL Mode=Disable;Include Error Detail=false"
        Write-FixtureFile $runtimeSettings (@{ConnectionStrings = @{$key = $connection}} | ConvertTo-Json -Compress)
    } finally {
        $connection = $null
        $runtimeValue = $null
    }
    $name = "$project-bootstrap-$service"
    $inputs = [ordered]@{
        '/run/setup/request.json' = $requestFile
        '/run/secrets/migrator-password' = $migratorFile
        '/run/secrets/runtime-password' = $runtimeFile
        '/run/secrets/runtime-config.json' = $runtimeSettings
        "/run/source/bootstrap-$service-roles.sql" = Join-Path $bootstrapRoot "bootstrap-$service-roles.sql"
    }
    $argsList = @('run', '--name', $name, '--network', $state.internalNetwork,
        '--label', "com.diliwo.zeka.mvp02=$FixtureId", '--cap-drop', 'ALL',
        '--security-opt', 'no-new-privileges=true', '--read-only', '--tmpfs', '/tmp:rw,noexec,nosuid,nodev,size=64m',
        '--log-driver', 'none', '--mount', "type=bind,src=$($adminMount.DaemonPath),dst=/run/secrets/admin-password,readonly")
    foreach ($entry in $inputs.GetEnumerator()) {
        $mount = Get-W3MountSource -WindowsPath $entry.Value
        $argsList += @('--mount', "type=bind,src=$($mount.DaemonPath),dst=$($entry.Key),readonly")
    }
    $resultMount = Get-W3MountSource -WindowsPath $results
    $argsList += @('--mount', "type=bind,src=$($resultMount.DaemonPath),dst=/run/results", $SetupImage)

    $null = @(& docker --context default @argsList 2>&1)
    $native = $LASTEXITCODE
    $resultFile = Join-Path $results 'result.json'
    if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
        throw "ZEK52_DATABASE_RESULT_ABSENT ($service native $native)"
    }
    $scan = @(sonar analyze secrets $resultFile 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ZEK52_DATABASE_RESULT_SCAN_REJECTED ($service native $native)" }
    $result = ConvertFrom-Json -AsHashtable -InputObject (Get-Content -LiteralPath $resultFile -Raw)
    $safeReceipt = [ordered]@{
        service = $service; native = $native; status = Get-SafeResultCode $result.status
        stage = Get-SafeResultCode $result.stage; code = Get-SafeResultCode $result.code
        sqlState = if ($result.sqlState -cmatch '^[0-9A-Z]{5}$') { $result.sqlState } else { $null }
    }
    $safeReceipt | ConvertTo-Json -Compress
    if ($native -ne 0 -or $result.status -cne 'INITIALIZED' -or $result.service -cne $service -or
        $result.clusterId -cnotmatch '^[0-9]{1,20}$' -or ($clusterId -and $clusterId -cne $result.clusterId)) {
        throw "ZEK52_DATABASE_SETUP_NOT_ADMITTED ($service)"
    }
    $clusterId = [string]$result.clusterId
}
Write-Output 'ZEK52_DATABASES_INITIALIZED'
