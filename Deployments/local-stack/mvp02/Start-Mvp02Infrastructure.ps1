#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}$')][string]$FixtureId)

$ErrorActionPreference = 'Stop'
$engineId = '0b62f5ec-15d9-4e4b-a450-817d52378eef'
$project = "zeka-mvp02-$FixtureId"
$internalNetwork = "${project}_internal"
$edgeNetwork = "${project}_edge"
$privateRoot = "D:\Workspace\ZekaLocal\$project"
$statePath = Join-Path $privateRoot 'infrastructure.json'
$encoding = [Text.UTF8Encoding]::new($false)

function Invoke-FixtureDocker {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = @(& docker --context default @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "ZEK52_DOCKER_FAILED_$($Arguments[0].ToUpperInvariant()) (native $LASTEXITCODE)"
    }
    return $output
}

function New-FixturePassword {
    [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}

function Write-NewFile {
    param([string]$Path, [string]$Value)
    $data = $encoding.GetBytes($Value)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($data, 0, $data.Length) }
    finally { $stream.Dispose(); [Array]::Clear($data) }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.Name -ine 'DILI_LP\herve') { throw 'ZEK52_OWNER_WINDOWS_REQUIRED' }
if ((docker context show) -cne 'default' -or $LASTEXITCODE -ne 0) { throw 'ZEK52_DOCKER_CONTEXT_REJECTED' }
if ((Invoke-FixtureDocker -Arguments @('info', '--format', '{{.ID}}')) -cne $engineId) { throw 'ZEK52_ENGINE_REJECTED' }
if ((Test-Path -LiteralPath $privateRoot) -or (Test-Path -LiteralPath $statePath)) { throw 'ZEK52_FIXTURE_ALREADY_EXISTS' }
$resources = @(
    Invoke-FixtureDocker -Arguments @('ps', '-a', '--format', '{{.Names}}|{{.Label "com.docker.compose.project"}}')
    Invoke-FixtureDocker -Arguments @('volume', 'ls', '--format', '{{.Name}}|{{.Label "com.docker.compose.project"}}')
    Invoke-FixtureDocker -Arguments @('network', 'ls', '--format', '{{.Name}}|{{.Label "com.docker.compose.project"}}')
)
if ($resources -match ('(^|\|)' + [regex]::Escape($project) + '(?:[_-]|\||$)')) { throw 'ZEK52_DOCKER_FIXTURE_ALREADY_EXISTS' }

# These explicit ranges avoid the exhausted automatic pools. Recheck the current
# Docker, Rancher and Windows routes before using this fixed workstation fixture.
$internalSubnet = '10.248.52.0/28'
$edgeSubnet = '10.248.53.0/28'
function Test-OverlappingRange {
    param([string]$Range, [string]$Candidate)
    function Get-Interval([string]$Prefix) {
        $parts = $Prefix.Split('/')
        $bytes = [Net.IPAddress]::Parse($parts[0]).GetAddressBytes()
        $bits = [int]$parts[1]
        if ($bytes.Length -ne 4 -or $bits -lt 0 -or $bits -gt 32) { throw 'ZEK52_NETWORK_METADATA_REJECTED' }
        $number = [long]$bytes[0] * 16777216 + [long]$bytes[1] * 65536 + [long]$bytes[2] * 256 + $bytes[3]
        $size = [long]1 -shl (32 - $bits)
        $first = $number - ($number % $size)
        return @($first, ($first + $size - 1))
    }
    $a = Get-Interval $Range
    $b = Get-Interval $Candidate
    return $a[0] -le $b[1] -and $b[0] -le $a[1]
}
$bridges = @(Invoke-FixtureDocker -Arguments @('network', 'ls', '--format', '{{.ID}}'))
$dockerSubnets = Invoke-FixtureDocker -Arguments (@('network', 'inspect', '--format', '{{range .IPAM.Config}}{{.Subnet}} {{end}}') + $bridges)
$rancherRoutes = @(& rdctl shell ip -4 route show table main 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'ZEK52_RANCHER_ROUTE_TABLE_UNAVAILABLE' }
$windowsRoutes = Get-NetRoute -AddressFamily IPv4 | Select-Object -ExpandProperty DestinationPrefix
$observedRanges = @(
    ($dockerSubnets -join ' ') -split '\s+' | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' }
    $rancherRoutes | ForEach-Object { if ($_ -match '^(\d+\.\d+\.\d+\.\d+/\d+)\b') { $Matches[1] } }
    $windowsRoutes | Where-Object { $_ -ne '0.0.0.0/0' }
)
if ($observedRanges.Count -eq 0) { throw 'ZEK52_NETWORK_METADATA_UNAVAILABLE' }
foreach ($range in $observedRanges) {
    if ((Test-OverlappingRange $range $internalSubnet) -or (Test-OverlappingRange $range $edgeSubnet)) {
        throw 'ZEK52_NETWORK_RANGE_OCCUPIED'
    }
}

[IO.Directory]::CreateDirectory($privateRoot) | Out-Null
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($identity.User)
$acl.SetAccessRuleProtection($true, $false)
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
foreach ($sid in @($identity.User, $system)) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $privateRoot -AclObject $acl

$adminPath = Join-Path $privateRoot 'postgres-admin'
$brokerPassword = Join-Path $privateRoot 'broker-password'
$brokerPath = Join-Path $privateRoot 'rabbitmq.conf'
Write-NewFile -Path $adminPath -Value (New-FixturePassword)
$brokerSecret = New-FixturePassword
try {
    Write-NewFile -Path $brokerPassword -Value $brokerSecret
    Write-NewFile -Path $brokerPath -Value ("default_user = mvp02-local" + [char]10 + "default_pass = $brokerSecret" + [char]10)
} finally { $brokerSecret = $null }

$env:ZEKA_MVP02_INTERNAL_NETWORK = $internalNetwork
$env:ZEKA_MVP02_POSTGRES_FILE = $adminPath
$env:ZEKA_MVP02_BROKER_FILE = $brokerPath
$composeArgs = @('compose', '--project-name', $project, '--file', (Join-Path $PSScriptRoot 'compose.infrastructure.yaml'))
Invoke-FixtureDocker -Arguments ($composeArgs + @('config', '--quiet')) | Out-Null
$internalId = Invoke-FixtureDocker -Arguments @('network', 'create', '--driver', 'bridge', '--internal', '--subnet', $internalSubnet, '--label', "com.diliwo.zeka.mvp02=$FixtureId", $internalNetwork)
$edgeId = Invoke-FixtureDocker -Arguments @('network', 'create', '--driver', 'bridge', '--subnet', $edgeSubnet, '--label', "com.diliwo.zeka.mvp02=$FixtureId", $edgeNetwork)
Invoke-FixtureDocker -Arguments ($composeArgs + @('up', '-d', '--no-recreate', '--wait', '--wait-timeout', '90', 'postgres', 'rabbitmq')) | Out-Null

$containers = Invoke-FixtureDocker -Arguments @('ps', '--filter', "label=com.docker.compose.project=$project", '--format', '{{.ID}}|{{.Names}}|{{.Status}}|{{.Ports}}')
if (@($containers).Count -ne 2 -or @($containers | Where-Object { $_ -match '->' -or $_ -notmatch '\(healthy\)' }).Count -ne 0) { throw 'ZEK52_INFRASTRUCTURE_ADMISSION_FAILED' }
$state = [ordered]@{
    fixtureId = $FixtureId
    project = $project
    engineId = $engineId
    internalNetwork = $internalNetwork
    internalNetworkId = [string]$internalId
    edgeNetwork = $edgeNetwork
    edgeNetworkId = [string]$edgeId
    initialisationId = [Guid]::NewGuid().ToString('D')
    containers = @($containers)
}
Write-NewFile -Path $statePath -Value ($state | ConvertTo-Json -Depth 4)
$state | ConvertTo-Json -Depth 4
