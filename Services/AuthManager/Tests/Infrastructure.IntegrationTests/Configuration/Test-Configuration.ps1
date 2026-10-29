[CmdletBinding()]
param([string]$ReportFile)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../..'))
$deployment = Join-Path $root 'Deployments/local-stack'
$checks = [Collections.Generic.List[string]]::new()
function Require([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $checks.Add($Name)
}
function Stop-OwnedProcess([Diagnostics.Process]$Process) {
    if (!$Process.HasExited) { $Process.Kill($true) }
    if (!$Process.WaitForExit(5000)) { throw 'owned_child_cleanup_timeout' }
}
function Admit([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'source_file_missing' }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command sonar -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['LOG_LEVEL'] = 'SILENT'
    foreach ($arg in @('analyze', 'secrets', $Path)) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $null = $process.Start()
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000)) {
        Stop-OwnedProcess $process
        throw 'source_admission_timeout'
    }
    if (!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($out, $err))).Wait(5000)) {
        Stop-OwnedProcess $process
        throw 'source_admission_stream_timeout'
    }
    $scan = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or !$scan.Contains('No secrets found') -or $scan.Contains('Scan warning:')) {
        throw 'source_admission_blocked'
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
foreach ($name in @('compose.yaml', 'http-healthcheck.sh', 'ocelot.json')) {
    $null = Admit (Join-Path $deployment $name)
}

# Deliberately invalid, non-sensitive inputs: config parsing never starts a service.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('zeka-m1-config-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$relativeFiles = @(
    'postgres/admin-password', 'rabbitmq/rabbitmq.conf',
    'auth/appsettings.LocalStack.json', 'auth/signing-key.pem',
    'auth/browser-session-hash.key', 'adminarea/appsettings.LocalStack.json',
    'client/appsettings.LocalStack.json', 'gateway/appsettings.LocalStack.json',
    'trust/ca-bundle.pem', 'build/github-token'
)
foreach ($relative in $relativeFiles) {
    $path = Join-Path $fixture $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    $value = if ($relative.EndsWith('.json')) { '{}' } else { 'NON_SENSITIVE_CONFIG_FIXTURE_NOT_FOR_AUTHENTICATION' }
    [IO.File]::WriteAllText($path, $value, [Text.UTF8Encoding]::new($false))
    $null = Admit $path
}
$emptyEnv = Join-Path $fixture 'empty.env'
[IO.File]::WriteAllText($emptyEnv, '', [Text.UTF8Encoding]::new($false))
$null = Admit $emptyEnv
function Compose([bool]$WithPrivatePath, [bool]$Quiet) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment.Clear()
    $routing = @{
        SystemRoot = [Environment]::GetFolderPath('Windows')
        USERPROFILE = [Environment]::GetFolderPath('UserProfile')
        APPDATA = [Environment]::GetFolderPath('ApplicationData')
        LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData')
        TEMP = [IO.Path]::GetTempPath()
        TMP = [IO.Path]::GetTempPath()
    }
    foreach ($entry in $routing.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    if ($WithPrivatePath) { $start.Environment['ZEKA_M1_PRIVATE_DIR'] = $fixture }
    $arguments = @('--context', 'default', 'compose', '--env-file', $emptyEnv,
        '--project-name', 'zeka-m1', '-f', (Join-Path $deployment 'compose.yaml'),
        '--profile', 'applications', 'config')
    if ($Quiet) { $arguments += '--quiet' } else { $arguments += @('--format', 'json') }
    foreach ($arg in $arguments) { $start.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $null = $process.Start()
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000)) {
        Stop-OwnedProcess $process
        throw 'compose_config_timeout'
    }
    if (!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($out, $err))).Wait(5000)) {
        Stop-OwnedProcess $process
        throw 'compose_config_stream_timeout'
    }
    $discard = $err.Result
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $out.Result }
}

$status = 'passed'
$failure = $null
try {
    $missing = Compose $false $true
    Require ($missing.ExitCode -ne 0) 'private_configuration_is_required'
    $missing = $null
    $parsed = Compose $true $false
    Require ($parsed.ExitCode -eq 0) 'compose_configuration_parses'
    $model = $parsed.Output | ConvertFrom-Json -Depth 60
    $parsed = $null
    Require ($model.name -ceq 'zeka-m1') 'explicit_project_identity'
    $names = @($model.services.psobject.Properties.Name | Sort-Object)
    Require (($names -join ',') -ceq 'adminarea,auth,client,gateway,postgres,rabbitmq') 'six_expected_services'
    $totalMemory = 0L
    foreach ($name in $names) {
        $service = $model.services.$name
        Require (!($service.psobject.Properties.Name -contains 'container_name')) ($name + '_has_project_owned_name')
        Require (@($service.networks.psobject.Properties.Name).Count -eq 1 -and ($service.networks.psobject.Properties.Name -contains 'internal')) ($name + '_uses_only_internal_network')
        Require ($null -ne $service.healthcheck -and $service.healthcheck.test.Count -gt 0) ($name + '_has_health_probe')
        Require ($service.logging.driver -ceq 'none') ($name + '_has_no_retained_raw_logs')
        $totalMemory += [long]$service.mem_limit
        foreach ($key in $service.environment.psobject.Properties.Name) {
            Require (!($key -match '(?i)password|token|connectionstring') -or $key.EndsWith('_FILE')) ($name + '_environment_' + $key + '_is_nonsecret')
        }
        foreach ($port in @($service.ports)) {
            if ($null -ne $port) {
                Require ($name -ceq 'gateway' -and $port.host_ip -ceq '127.0.0.1' -and [string]$port.published -ceq '19000' -and $port.target -eq 8080) 'only_loopback_gateway_port'
            }
        }
        if ($name -in @('auth', 'adminarea', 'client', 'gateway')) {
            Require ($service.environment.ASPNETCORE_ENVIRONMENT -ceq 'LocalStack') ($name + '_uses_localstack_environment')
            Require ('applications' -in $service.profiles) ($name + '_startup_is_explicitly_held')
            Require ((Test-Path -LiteralPath (Join-Path $service.build.context $service.build.dockerfile) -PathType Leaf)) ($name + '_reuses_existing_dockerfile')
            Require (@($service.secrets | Where-Object { $_.target -ceq '/app/appsettings.LocalStack.json' }).Count -eq 1) ($name + '_uses_private_configuration_file')
            Require (@($service.secrets | Where-Object { $_.source -ceq 'github_token' }).Count -eq 0) ($name + '_has_no_runtime_build_credential')
        }
        foreach ($dependency in $service.depends_on.psobject.Properties.Name) {
            Require ($dependency -in $names) ($name + '_dependency_' + $dependency + '_resolves')
        }
    }
    Require ($totalMemory -eq 3489660928) 'configured_memory_budget_is_3_25_gib'
    Require ($model.networks.internal.internal -eq $true -and !$model.networks.internal.external) 'project_network_is_internal_and_owned'
    foreach ($volume in $model.volumes.psobject.Properties) {
        Require (!$volume.Value.external -and $volume.Value.name.StartsWith('zeka-m1_')) ('owned_volume_' + $volume.Name)
    }
    Require (@($model.services.postgres.ports | Where-Object { $null -ne $_ }).Count -eq 0 -and @($model.services.rabbitmq.ports | Where-Object { $null -ne $_ }).Count -eq 0) 'database_and_broker_are_unpublished'
    Require (@($model.services.postgres.volumes | Where-Object { $_.target -like '*docker-entrypoint-initdb.d*' }).Count -eq 0) 'no_automatic_database_bootstrap_or_migration'
    foreach ($secret in $model.secrets.psobject.Properties) {
        Require ($secret.Value.file.StartsWith($fixture)) ('protected_file_interface_' + $secret.Name)
    }
    $routes = (Get-Content -LiteralPath (Join-Path $deployment 'ocelot.json') -Raw | ConvertFrom-Json).Routes
    Require ($routes.Count -eq 6) 'six_existing_browser_routes_only'
    foreach ($route in $routes) {
        Require ($route.Key -like 'GatewayBrowserTransport:*' -and !$route.DangerousAcceptAnyServerCertificateValidator -and $route.RouteIsCaseSensitive -and $route.FileCacheOptions.TtlSeconds -eq 0 -and !$route.HttpHandlerOptions.AllowAutoRedirect -and !$route.HttpHandlerOptions.UseCookieContainer -and !$route.HttpHandlerOptions.UseProxy) ('gateway_route_' + $route.Key + '_preserves_admission')
    }
} catch {
    $status = 'failed'
    $failure = if ($_.Exception.Message -match '^[A-Za-z0-9_:-]+$') { $_.Exception.Message } else { 'configuration_validation_failed' }
}
$result = [ordered]@{
    status = $status
    failure = $failure
    assertionsPassed = $checks.Count
    assertions = $checks.ToArray()
    syntheticInputsOnly = $true
    fixtureRetained = $fixture
    resourcesCreated = $false
    runtimeReadinessEstablished = $false
    migrationsExecuted = $false
}
if ($ReportFile) { [IO.File]::WriteAllText($ReportFile, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false)) }
$result | ConvertTo-Json -Depth 8 -Compress
if ($status -ne 'passed') { exit 1 }
exit 0
