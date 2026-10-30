[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$PrivateDirectory = $env:ZEKA_M1_PRIVATE_DIR,
    [string]$ProjectName = 'zeka-m1',
    [switch]$Fixture
)
$ErrorActionPreference = 'Stop'
$stage = 'host-admission'
$lock = $null
$progress = $null
$progressFile = $null
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$expectedOwnerRoot = 'C:\Users\herve\AppData\Local\Zeka\M1\private'
. (Join-Path $PSScriptRoot 'W3-NativeDiagnostics.ps1')
$diagnosticPath = Join-Path ([IO.Path]::GetFullPath($BuildDirectory)) ('native-diagnostics-' + [Guid]::NewGuid().ToString('N'))
Initialize-W3Diagnostics -Directory $diagnosticPath -FixtureId $ProjectName -EngineId '0b62f5ec-15d9-4e4b-a450-817d52378eef' | Out-Null
function Fail([string]$Code) { Write-W3GuardRecord -Rule $Code -Operation $stage; throw [InvalidOperationException]::new($Code) }
function Native([string]$Exe, [string[]]$Argv, [int]$Milliseconds = 120000, [bool]$AllowFailure = $false) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Exe)
    $verb = $Argv[0]
    if ($name -eq 'docker' -and $verb -in @('volume','network','image','container')) { $verb += '.' + $Argv[1] }
    return Invoke-W3Native -Exe $Exe -ArgumentList $Argv -WorkingDirectory $repo -TimeoutMs $Milliseconds -Operation ($stage + '.' + $name + '.' + $verb) -AllowFailure:$AllowFailure
}
function Restricted([string]$Path, [bool]$Protect = $false) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 'PRIVATE_REPARSE_REJECTED' }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $allowed = @($owner.Value, 'S-1-5-18', 'S-1-5-32-544') | Sort-Object -Unique
    if ($Protect) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetOwner($owner)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($identity in $allowed) {
            $sid = [Security.Principal.SecurityIdentifier]::new($identity)
            $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    $actual = Get-Acl -LiteralPath $Path
    $rules = $actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    $unexpected = @($rules | Where-Object { $_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow })
    if ($actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner.Value -or $rules.Count -ne $allowed.Count -or $unexpected.Count -ne 0) { Fail 'PRIVATE_ACL_REJECTED' }
    if ($Protect -and !$actual.AreAccessRulesProtected) { Fail 'PRIVATE_INHERITANCE_REJECTED' }
}
function SaveBytes([string]$Path, [byte[]]$Bytes, [bool]$Replace = $false) {
    Restricted (Split-Path -Parent $Path)
    if ((Test-Path -LiteralPath $Path) -and !$Replace) { Fail 'PRIVATE_OVERWRITE_REJECTED' }
    if (Test-Path -LiteralPath $Path) { Restricted $Path }
    $temp = Join-Path (Split-Path -Parent $Path) ('.write-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    Restricted $temp
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, $null) } else { [IO.File]::Move($temp, $Path) }
    Restricted $Path
    if ((Get-Item -LiteralPath $Path).Length -ne $Bytes.Length) { Fail 'PRIVATE_WRITE_INCOMPLETE' }
}
function SaveJson([string]$Path, $Value, [bool]$Replace = $false) {
    SaveBytes $Path ([Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 18))) $Replace
}
function NewPassword([string]$Path) {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    try { SaveBytes $Path ([Text.Encoding]::UTF8.GetBytes([Convert]::ToHexString($bytes))) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}
function HashFile([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
try {
    if (!$IsWindows) { Fail 'OWNER_WINDOWS_REQUIRED' }
    if ($ProjectName -ne 'zeka-m1' -and !($Fixture -and $ProjectName -match '^zeka-m1-bootstrap-fixture-[a-f0-9]{8}$')) { Fail 'PROJECT_SCOPE_REJECTED' }
    if ($Fixture -and $ProjectName -eq 'zeka-m1') { Fail 'FIXTURE_ACTUAL_PROJECT_REJECTED' }
    if ([string]::IsNullOrWhiteSpace($PrivateDirectory)) { Fail 'PRIVATE_ROOT_MISSING' }
    $private = [IO.Path]::GetFullPath($PrivateDirectory)
    if (!$Fixture -and ![StringComparer]::OrdinalIgnoreCase.Equals($private, $expectedOwnerRoot)) { Fail 'PRIVATE_ROOT_REJECTED' }
    if ($Fixture -and [StringComparer]::OrdinalIgnoreCase.Equals($private, $expectedOwnerRoot)) { Fail 'FIXTURE_OWNER_PRIVATE_ROOT_REJECTED' }
    if ($private.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Fail 'PRIVATE_ROOT_IN_BUILD_CONTEXT' }
    if (!(Test-Path -LiteralPath $private)) {
        if (!$Fixture) { Fail 'PRIVATE_ROOT_MISSING' }
        New-Item -ItemType Directory -Path $private -Force | Out-Null
        Restricted $private $true
    }
    Restricted $private
    if (!(Get-Acl -LiteralPath $private).AreAccessRulesProtected) { Fail 'PRIVATE_ROOT_INHERITANCE_REJECTED' }
    foreach ($name in @('database', 'postgres', 'auth', 'adminarea', 'client')) {
        New-Item -ItemType Directory -Path (Join-Path $private $name) -Force | Out-Null
        Restricted (Join-Path $private $name)
    }
    $lock = [IO.FileStream]::new((Join-Path $private 'database\setup.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $env:ZEKA_M1_PRIVATE_DIR = $private
    $source = (Native 'git' @('rev-parse', 'HEAD')).Out.Trim()
    if ($source -notmatch '^[0-9a-f]{40}$') { Fail 'SOURCE_COMMIT_REJECTED' }
    if ((Native 'git' @('status', '--porcelain', '--untracked-files=all')).Out.Trim()) { Fail 'SOURCE_WORKTREE_NOT_CLEAN' }
    $engine = (Native 'docker' @('info', '--format', '{{.ID}}')).Out.Trim()
    $context = (Native 'docker' @('context', 'show')).Out.Trim()
    if ($context -ne 'default' -or $engine -ne '0b62f5ec-15d9-4e4b-a450-817d52378eef') { Fail 'DOCKER_TARGET_REJECTED' }
    $adminFile = Join-Path $private 'postgres\admin-password'
    if (!(Test-Path -LiteralPath $adminFile)) {
        if (!$Fixture) { Fail 'OWNER_ADMINISTRATOR_FILE_MISSING' }
        NewPassword $adminFile
    }
    Restricted $adminFile
    Register-W3Secret -Value ([IO.File]::ReadAllText($adminFile).Trim())
    $progressFile = Join-Path $private 'database\setup-progress.json'
    if (Test-Path -LiteralPath (Join-Path $private 'database\reset-incomplete.json')) { Fail 'RESET_INCOMPLETE' }
    $volume = $ProjectName + '_postgres_data'
    $container = $ProjectName + '-postgres-1'
    $volumeObservation = Native 'docker' @('volume', 'inspect', $volume) 15000 $true
    $containerObservation = Native 'docker' @('inspect', $container) 15000 $true
    $networkName = $ProjectName + '_internal'
    $networkObservation = Native 'docker' @('network', 'inspect', $networkName) 15000 $true
    if ($volumeObservation.Exit -ne 0 -and $volumeObservation.Error -notmatch '(?i)(no such volume|not found)') { Fail 'VOLUME_OBSERVATION_UNCERTAIN' }
    if ($containerObservation.Exit -ne 0 -and $containerObservation.Error -notmatch '(?i)(no such object|no such container|not found)') { Fail 'CONTAINER_OBSERVATION_UNCERTAIN' }
    if ($networkObservation.Exit -ne 0 -and $networkObservation.Error -notmatch '(?i)(no such network|not found)') { Fail 'NETWORK_OBSERVATION_UNCERTAIN' }
    $resourcesEstablished = $volumeObservation.Exit -eq 0 -or $containerObservation.Exit -eq 0
    $templatePath = Join-Path $PSScriptRoot 'configuration\database-settings.template.json'
    $template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json -AsHashtable
    $templateHash = HashFile $templatePath
    $roleHashes = @{}
    foreach ($service in @('auth', 'adminarea', 'client')) { $roleHashes[$service] = HashFile (Join-Path $repo ('Deployments\database\bootstrap-' + $service + '-roles.sql')) }
    if (Test-Path -LiteralPath $progressFile) {
        Restricted $progressFile
        $progress = Get-Content -LiteralPath $progressFile -Raw | ConvertFrom-Json -AsHashtable
        if ($progress.schema -ne 'zeka-m1-database-setup/v1' -or $progress.project -ne $ProjectName -or $progress.sourceCommit -ne $source -or $progress.engineId -ne $engine -or $progress.templateHash -ne $templateHash) { Fail 'INITIALIZATION_PROGRESS_IDENTITY_DRIFT' }
        foreach ($key in $roleHashes.Keys) { if ($progress.roleSqlHashes[$key] -ne $roleHashes[$key]) { Fail 'ROLE_SOURCE_DRIFT' } }
        if (($progress.containerId -or $progress.clusterId) -and ($volumeObservation.Exit -ne 0 -or $containerObservation.Exit -ne 0)) { Fail 'ESTABLISHED_POSTGRES_RESOURCE_MISSING' }
        if ($progress.requiresInspection) { Fail 'PREVIOUS_OPERATION_REQUIRES_INSPECTION' }
    } else {
        if ($resourcesEstablished -or $networkObservation.Exit -eq 0) { Fail 'ESTABLISHED_RESOURCE_WITHOUT_PROGRESS' }
        $progress = @{ schema = 'zeka-m1-database-setup/v1'; project = $ProjectName; sourceCommit = $source; engineId = $engine; initializationId = [Guid]::NewGuid().ToString('D'); templateHash = $templateHash; roleSqlHashes = $roleHashes; clusterId = $null; services = @{}; requiresInspection = $false }
        SaveJson $progressFile $progress
    }
    if ($networkObservation.Exit -eq 0) {
        $n = ($networkObservation.Out | ConvertFrom-Json)[0]
        if (!$n.Internal -or $n.Driver -ne 'bridge' -or $n.Labels.'com.docker.compose.project' -ne $ProjectName -or $n.Labels.'com.diliwo.zeka.initialization-id' -ne $progress.initializationId -or $n.Labels.'com.diliwo.zeka.scope' -ne 'm1-database-bootstrap') { Fail 'NETWORK_INITIALIZATION_IDENTITY_DRIFT' }
    }
    if ($volumeObservation.Exit -eq 0) {
        $v = ($volumeObservation.Out | ConvertFrom-Json)[0]
        if ($v.Labels.'com.docker.compose.project' -ne $ProjectName -or $v.Labels.'com.diliwo.zeka.initialization-id' -ne $progress.initializationId) { Fail 'VOLUME_INITIALIZATION_IDENTITY_DRIFT' }
    }
    if ($containerObservation.Exit -eq 0) {
        $c = ($containerObservation.Out | ConvertFrom-Json)[0]
        $bindings = @($c.HostConfig.PortBindings.PSObject.Properties)
        if ($c.Config.Labels.'com.docker.compose.project' -ne $ProjectName -or $c.Config.Labels.'com.diliwo.zeka.initialization-id' -ne $progress.initializationId -or $bindings.Count -gt 0 -or $c.HostConfig.NetworkMode -eq 'host') { Fail 'CONTAINER_IDENTITY_OR_EXPOSURE_DRIFT' }
        $networks = @($c.NetworkSettings.Networks.PSObject.Properties)
        $dataMounts = @($c.Mounts | Where-Object { $_.Destination -eq '/var/lib/postgresql/data' })
        if ($networks.Count -ne 1 -or $networks[0].Name -ne $networkName -or $dataMounts.Count -ne 1 -or $dataMounts[0].Type -ne 'volume' -or $dataMounts[0].Name -ne $volume -or !$dataMounts[0].RW -or $c.Config.Image -ne 'postgres:17') { Fail 'CONTAINER_NETWORK_OR_VOLUME_DRIFT' }
        if ($volumeObservation.Exit -ne 0 -or $networkObservation.Exit -ne 0) { Fail 'CONTAINER_RESOURCE_STATE_UNCERTAIN' }
        if ($progress.containerId -and $progress.containerId -ne $c.Id) { Fail 'POSTGRES_CONTAINER_ID_DRIFT' }
    }
    $stage = 'credential-save-before-apply'
    foreach ($service in @('auth', 'adminarea', 'client')) {
        $spec = $template.services[$service]
        $directory = Join-Path $private $service
        if (!$progress.services.ContainsKey($service)) { $progress.services[$service] = @{ credentialsSaved = $false; completed = $false } }
        $state = $progress.services[$service]
        foreach ($kind in @('migrator', 'runtime')) {
            $file = Join-Path $directory ($kind + '-password')
            if (!(Test-Path -LiteralPath $file)) {
                if ($resourcesEstablished -or $state.credentialsSaved -or $state.completed) { Fail 'ESTABLISHED_CREDENTIAL_FILE_MISSING' }
                NewPassword $file
            }
            Restricted $file
            $value = [IO.File]::ReadAllText($file).Trim()
            Register-W3Secret -Value $value
            if ($value.Length -ne 64 -or $value -notmatch '^[0-9A-Fa-f]{64}$') { Fail 'SAVED_CREDENTIAL_FORMAT_REJECTED' }
        }
        $runtime = [IO.File]::ReadAllText((Join-Path $directory 'runtime-password')).Trim()
        $connection = 'Host=postgres;Port=5432;Database=' + $spec.database + ';Username=' + $spec.runtime + ';Password=' + $runtime + ';Pooling=false;Timeout=5;Command Timeout=30;SSL Mode=Disable;Include Error Detail=false'
        $rendered = @{ ConnectionStrings = @{} }
        $rendered.ConnectionStrings[$spec.connectionStringKey] = $connection
        $runtimeFile = Join-Path $directory 'database.LocalStack.json'
        if (Test-Path -LiteralPath $runtimeFile) {
            Restricted $runtimeFile
            $existing = Get-Content -LiteralPath $runtimeFile -Raw | ConvertFrom-Json -AsHashtable
            if ($existing.ConnectionStrings[$spec.connectionStringKey] -ne $connection) { Fail 'RUNTIME_CONFIGURATION_DRIFT' }
        } else {
            if ($state.completed) { Fail 'ESTABLISHED_RUNTIME_CONFIGURATION_MISSING' }
            SaveJson $runtimeFile $rendered
        }
        $state.credentialsSaved = $true
        SaveJson $progressFile $progress $true
        $runtime = $null
        $connection = $null
        $value = $null
    }
    $stage = 'postgres-create-or-resume'
    $env:ZEKA_M1_INITIALIZATION_ID = $progress.initializationId
    $compose = Join-Path $PSScriptRoot 'compose.database-setup.yaml'
    $adminMount = Get-W3MountSource -WindowsPath $adminFile
    $env:ZEKA_M1_POSTGRES_ADMIN_SOURCE = $adminMount.WindowsPhysical
    $null = Native 'docker' @('compose', '--project-name', $ProjectName, '-f', $compose, 'up', '-d', '--wait', '--wait-timeout', '60', 'postgres') 90000
    $current = (((Native 'docker' @('inspect', $container)).Out) | ConvertFrom-Json)[0]
    if ($current.Config.Labels.'com.diliwo.zeka.initialization-id' -ne $progress.initializationId -or $current.NetworkSettings.Ports.'5432/tcp' -or $current.HostConfig.NetworkMode -eq 'host') { Fail 'POSTGRES_RESOURCE_ADMISSION_FAILED' }
    $currentNetworks = @($current.NetworkSettings.Networks.PSObject.Properties)
    $currentMounts = @($current.Mounts | Where-Object { $_.Destination -eq '/var/lib/postgresql/data' })
    if ($currentNetworks.Count -ne 1 -or $currentNetworks[0].Name -ne $networkName -or $currentMounts.Count -ne 1 -or $currentMounts[0].Type -ne 'volume' -or $currentMounts[0].Name -ne $volume -or !$currentMounts[0].RW -or $current.Config.Image -ne 'postgres:17') { Fail 'POSTGRES_NETWORK_OR_VOLUME_ADMISSION_FAILED' }
    $admittedNetwork = (((Native 'docker' @('network', 'inspect', $networkName)).Out) | ConvertFrom-Json)[0]
    if (!$admittedNetwork.Internal -or $admittedNetwork.Driver -ne 'bridge' -or $admittedNetwork.Labels.'com.docker.compose.project' -ne $ProjectName -or $admittedNetwork.Labels.'com.diliwo.zeka.initialization-id' -ne $progress.initializationId) { Fail 'POSTGRES_NETWORK_ADMISSION_FAILED' }
    $progress.containerId = $current.Id
    SaveJson $progressFile $progress $true
    $stage = 'source-bound-runner-build'
    $build = [IO.Path]::GetFullPath($BuildDirectory)
    if ($build.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { Fail 'BUILD_OUTPUT_MUST_BE_OUTSIDE_CHECKOUT' }
    if ($private.StartsWith($build.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $build.StartsWith($private.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [StringComparer]::OrdinalIgnoreCase.Equals($private, $build)) { Fail 'BUILD_PRIVATE_OVERLAP_REJECTED' }
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    $image = 'zeka-m1-db-setup:' + $source.Substring(0, 12)
    $imageObservation = Native 'docker' @('image', 'inspect', $image) 15000 $true
    if ($imageObservation.Exit -ne 0) {
        if ($imageObservation.Error -notmatch '(?i)(no such image|not found)') { Fail 'IMAGE_OBSERVATION_UNCERTAIN' }
        $published = Join-Path $build 'published'
        $null = Native 'dotnet' @('publish', (Join-Path $repo 'Tools\Zeka.LocalStack.DatabaseSetup\Zeka.LocalStack.DatabaseSetup.csproj'), '--configuration', 'Release', '--output', $published, ('-p:ZekaSourceSha=' + $source)) 180000
        $null = Native 'docker' @('build', '--pull=false', '--label', ('org.opencontainers.image.revision=' + $source), '-f', (Join-Path $repo 'Tools\Zeka.LocalStack.DatabaseSetup\Dockerfile'), '-t', $image, $build) 120000
    }
    $imageInfo = (((Native 'docker' @('image', 'inspect', $image)).Out) | ConvertFrom-Json)[0]
    if ($imageInfo.Config.Labels.'org.opencontainers.image.revision' -ne $source) { Fail 'RUNNER_SOURCE_IDENTITY_DRIFT' }
    $stage = 'service-setup'
    $results = @()
    foreach ($service in @('auth', 'adminarea', 'client')) {
        $state = $progress.services[$service]
        $run = Join-Path $build ($service + '-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $run -Force | Out-Null
        $request = @{ Project = 'zeka-m1'; Service = $service; InitializationId = $progress.initializationId; SourceCommit = $source; Completed = $state.completed; ClusterId = $progress.clusterId; SchemaSha256 = $state.schemaSha256; HistorySha256 = $state.historySha256 }
        $request | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'request.json')
        $runnerName = $ProjectName + '-db-' + $service + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $requestMount = Get-W3MountSource -WindowsPath (Join-Path $run 'request.json')
        $resultMount = Get-W3MountSource -WindowsPath $run
        $migratorMount = Get-W3MountSource -WindowsPath (Join-Path $private ($service + '\migrator-password'))
        $runtimeMount = Get-W3MountSource -WindowsPath (Join-Path $private ($service + '\runtime-password'))
        $configurationMount = Get-W3MountSource -WindowsPath (Join-Path $private ($service + '\database.LocalStack.json'))
        $sqlMount = Get-W3MountSource -WindowsPath (Join-Path $repo ('Deployments\database\bootstrap-' + $service + '-roles.sql'))
        $parameters = @(
            'run', '--name', $runnerName, '--network', ($ProjectName + '_internal'),
            '--label', ('com.docker.compose.project=' + $ProjectName),
            '--label', ('com.diliwo.zeka.initialization-id=' + $progress.initializationId),
            '--mount', ('type=bind,src=' + $requestMount.DaemonPath + ',dst=/run/setup/request.json,readonly'),
            '--mount', ('type=bind,src=' + $resultMount.DaemonPath + ',dst=/run/results'),
            '--mount', ('type=bind,src=' + $adminMount.DaemonPath + ',dst=/run/secrets/admin-password,readonly'),
            '--mount', ('type=bind,src=' + $migratorMount.DaemonPath + ',dst=/run/secrets/migrator-password,readonly'),
            '--mount', ('type=bind,src=' + $runtimeMount.DaemonPath + ',dst=/run/secrets/runtime-password,readonly'),
            '--mount', ('type=bind,src=' + $configurationMount.DaemonPath + ',dst=/run/secrets/runtime-config.json,readonly'),
            '--mount', ('type=bind,src=' + $sqlMount.DaemonPath + ',dst=/run/source/bootstrap-' + $service + '-roles.sql,readonly'),
            $image
        )
        $native = Native 'docker' $parameters 330000 $true
        $resultFile = Join-Path $run 'result.json'
        if (!(Test-Path -LiteralPath $resultFile)) {
            $progress.requiresInspection = $true
            SaveJson $progressFile $progress $true
            Fail 'RUNNER_RESULT_MISSING_REQUIRES_INSPECTION'
        }
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json -AsHashtable
        $results += $result
        if ($native.Exit -ne 0 -or $result.status -eq 'BLOCKED') {
            $progress.requiresInspection = $true
            SaveJson $progressFile $progress $true
            Write-Output ($result | ConvertTo-Json -Compress)
            Fail 'SERVICE_SETUP_BLOCKED_REQUIRES_INSPECTION'
        }
        if ($state.completed -and $result.status -ne 'ALREADY_CURRENT') { Fail 'REPEAT_RESULT_REJECTED' }
        $progress.clusterId = $result.clusterId
        $state.completed = $true
        $state.schemaSha256 = $result.schemaSha256
        $state.historySha256 = $result.historySha256
        SaveJson $progressFile $progress $true
        Write-Output ($result | ConvertTo-Json -Compress)
    }
    $results | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $build ('database-result-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json'))
    Write-Output 'DATABASE_SETUP_VERIFIED; APPLICATION_CONFIGURATION_NOT_READY'
} catch {
    $failure = $_
    $code = 'GUARD_OR_OPERATION_REJECTED'
    $allowedGuardCodes = @('MOUNT_SOURCE_MISSING', 'MOUNT_SOURCE_HANDLE_REJECTED', 'MOUNT_SOURCE_PHYSICAL_PATH_UNAVAILABLE', 'MOUNT_SOURCE_LOCAL_DRIVE_REJECTED', 'MOUNT_SOURCE_TRANSLATION_REJECTED', 'MOUNT_SOURCE_WSL_UNREADABLE', 'DIAGNOSTICS_INSIDE_CHECKOUT_REJECTED', 'DIAGNOSTIC_IDENTITY_REJECTED', 'DIAGNOSTIC_REPARSE_REJECTED', 'DIAGNOSTICS_NOT_INITIALIZED', 'NATIVE_TIMEOUT_INPUT_REJECTED', 'W3_ARGUMENT_SECURITY_REJECTED', 'SOURCE_WORKTREE_NOT_CLEAN', 'ESTABLISHED_POSTGRES_RESOURCE_MISSING', 'PROCESS_START_FAILED', 'OPERATION_TIMEOUT_REQUIRES_INSPECTION', 'NATIVE_OPERATION_REJECTED', 'PRIVATE_REPARSE_REJECTED', 'PRIVATE_ACL_REJECTED', 'PRIVATE_INHERITANCE_REJECTED', 'PRIVATE_OVERWRITE_REJECTED', 'PRIVATE_WRITE_INCOMPLETE', 'OWNER_WINDOWS_REQUIRED', 'PROJECT_SCOPE_REJECTED', 'FIXTURE_ACTUAL_PROJECT_REJECTED', 'PRIVATE_ROOT_MISSING', 'PRIVATE_ROOT_REJECTED', 'FIXTURE_OWNER_PRIVATE_ROOT_REJECTED', 'PRIVATE_ROOT_IN_BUILD_CONTEXT', 'PRIVATE_ROOT_INHERITANCE_REJECTED', 'SOURCE_COMMIT_REJECTED', 'DOCKER_TARGET_REJECTED', 'OWNER_ADMINISTRATOR_FILE_MISSING', 'RESET_INCOMPLETE', 'VOLUME_OBSERVATION_UNCERTAIN', 'CONTAINER_OBSERVATION_UNCERTAIN', 'NETWORK_OBSERVATION_UNCERTAIN', 'INITIALIZATION_PROGRESS_IDENTITY_DRIFT', 'ROLE_SOURCE_DRIFT', 'PREVIOUS_OPERATION_REQUIRES_INSPECTION', 'ESTABLISHED_RESOURCE_WITHOUT_PROGRESS', 'NETWORK_INITIALIZATION_IDENTITY_DRIFT', 'VOLUME_INITIALIZATION_IDENTITY_DRIFT', 'CONTAINER_IDENTITY_OR_EXPOSURE_DRIFT', 'CONTAINER_NETWORK_OR_VOLUME_DRIFT', 'CONTAINER_RESOURCE_STATE_UNCERTAIN', 'POSTGRES_CONTAINER_ID_DRIFT', 'ESTABLISHED_CREDENTIAL_FILE_MISSING', 'SAVED_CREDENTIAL_FORMAT_REJECTED', 'RUNTIME_CONFIGURATION_DRIFT', 'ESTABLISHED_RUNTIME_CONFIGURATION_MISSING', 'POSTGRES_RESOURCE_ADMISSION_FAILED', 'POSTGRES_NETWORK_OR_VOLUME_ADMISSION_FAILED', 'POSTGRES_NETWORK_ADMISSION_FAILED', 'BUILD_OUTPUT_MUST_BE_OUTSIDE_CHECKOUT', 'BUILD_PRIVATE_OVERLAP_REJECTED', 'IMAGE_OBSERVATION_UNCERTAIN', 'RUNNER_SOURCE_IDENTITY_DRIFT', 'RUNNER_RESULT_MISSING_REQUIRES_INSPECTION', 'SERVICE_SETUP_BLOCKED_REQUIRES_INSPECTION', 'REPEAT_RESULT_REJECTED')
    if ($failure.Exception -is [InvalidOperationException] -and $failure.Exception.Message -in $allowedGuardCodes) { $code = $failure.Exception.Message }
    if ($progress -and $progressFile -and ($code -eq 'OPERATION_TIMEOUT_REQUIRES_INSPECTION' -or $stage -in @('postgres-create-or-resume', 'service-setup'))) {
        $progress.requiresInspection = $true
        try { SaveJson $progressFile $progress $true } catch { $code = 'INSPECTION_MARKER_WRITE_FAILED' }
    }
    Write-Output ('SETUP_BLOCKED stage=' + $stage + '; code=' + $code + '; type=' + $failure.Exception.GetType().Name)
    exit 1
} finally {
    if ($lock) { $lock.Dispose() }
}
