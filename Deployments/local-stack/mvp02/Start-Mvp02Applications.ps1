#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}$')][string]$FixtureId)

$ErrorActionPreference = 'Stop'
$fixture = "zeka-mvp02-$FixtureId"
$engine = '0b62f5ec-15d9-4e4b-a450-817d52378eef'
$root = "D:\Workspace\ZekaLocal\$fixture"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = Get-Acl -LiteralPath $root
if ($identity.Name -ine 'DILI_LP\herve' -or !$acl.AreAccessRulesProtected -or
    $acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $identity.User) {
    throw 'ZEK52_APPLICATION_PRIVATE_ROOT_REJECTED'
}

function Docker-Metadata([string[]]$Arguments) {
    $raw = @(& docker --context default @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ZEK52_APPLICATION_DOCKER_METADATA_FAILED (native $LASTEXITCODE)" }
    return $raw
}

if ((docker context show) -cne 'default' -or $LASTEXITCODE -ne 0 -or
    (Docker-Metadata @('info', '--format', '{{.ID}}')) -cne $engine) { throw 'ZEK52_APPLICATION_ENGINE_REJECTED' }
$statePath = Join-Path $root 'infrastructure.json'
$scan = @(sonar analyze secrets $statePath 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'ZEK52_APPLICATION_STATE_SCAN_REJECTED' }
$state = ConvertFrom-Json -AsHashtable -InputObject (Get-Content -LiteralPath $statePath -Raw)
if ($state.project -cne $fixture -or $state.fixtureId -cne $FixtureId -or $state.engineId -cne $engine) {
    throw 'ZEK52_APPLICATION_FIXTURE_IDENTITY_REJECTED'
}
$networkFormat = '{{.Id}}|{{index .Labels "com.diliwo.zeka.mvp02"}}|{{.Internal}}'
if ((Docker-Metadata @('network','inspect',$state.internalNetwork,'--format',$networkFormat)) -cne "$($state.internalNetworkId)|$FixtureId|true" -or
    (Docker-Metadata @('network','inspect',$state.edgeNetwork,'--format',$networkFormat)) -cne "$($state.edgeNetworkId)|$FixtureId|false") {
    throw 'ZEK52_APPLICATION_NETWORK_REJECTED'
}
$mailNetwork = 'zek27-validation-mail-20261117'
$mailContainer = Docker-Metadata @('container','inspect','mailpit','--format',
    '{{.Id}}|{{.State.Health.Status}}|{{with index .NetworkSettings.Networks "zek27-validation-mail-20261117"}}{{json .Aliases}}{{end}}')
if ($mailContainer -notmatch '^5ada0dbc45a999a652cf7fc9dc5aaa917ba25db368de7987486be8875876ff2f\|healthy\|\[.*"mailpit"') {
    throw 'ZEK52_MAILPIT_IDENTITY_REJECTED'
}
if ((Docker-Metadata @('network','inspect',$mailNetwork,'--format','{{.Id}}|{{.Internal}}')) -cne
    '32b2e1aee9b98f1617d870825b6290c07353a841efea8ef5334779ec4358e095|true') { throw 'ZEK52_MAILPIT_NETWORK_REJECTED' }

foreach ($dependency in @('postgres','rabbitmq')) {
    $serviceName = "$fixture-$dependency-1"
    $dependencyReady = $false
    $health = '{{index .Config.Labels "com.docker.compose.project"}}|{{.State.Status}}|{{.State.Health.Status}}'
    for ($i = 0; $i -lt 30; $i++) {
        $observed = Docker-Metadata @('container','inspect',$serviceName,'--format',$health)
        if ($observed -ceq "$fixture|running|healthy") { $dependencyReady = $true; break }
        if ($observed -cnotmatch ('^' + [regex]::Escape($fixture) + '\|running\|(?:starting|unhealthy)$')) { break }
        Start-Sleep -Seconds 1
    }
    if (!$dependencyReady) { throw "ZEK52_APPLICATION_DEPENDENCY_NOT_READY ($dependency)" }
}

$env:ZEKA_MVP02_RUNTIME_ROOT = $root
$env:ZEKA_MVP02_INTERNAL_NETWORK = $state.internalNetwork
$env:ZEKA_MVP02_EDGE_NETWORK = $state.edgeNetwork
$images = [ordered]@{}
foreach ($service in @('auth','client','gateway','frontend')) {
    $tag = if ($service -eq 'frontend') { "zeka-mvp02-frontend:$FixtureId" } else { "zeka-mvp02-$($service)-exact:$FixtureId" }
    $readback = Docker-Metadata @('image','inspect',$tag,'--format','{{.Id}}|{{index .Config.Labels "com.diliwo.zeka.mvp02"}}')
    if ($readback -notmatch '^sha256:[a-f0-9]{64}\|' -or ($readback -split '\|')[1] -cne $FixtureId) {
        throw 'ZEK52_APPLICATION_IMAGE_REJECTED'
    }
    $images[$service] = ($readback -split '\|')[0]
    [Environment]::SetEnvironmentVariable("ZEKA_MVP02_$($service.ToUpperInvariant())_IMAGE",$images[$service],'Process')
}

$existing = @(Docker-Metadata @('ps','-a','--filter',"label=com.docker.compose.project=$fixture",
    '--format','{{.ID}}|{{.Label "com.docker.compose.service"}}'))
foreach ($row in $existing) {
    $container, $service = $row -split '\|',2
    if ($images.Contains($service)) {
        $liveImage = Docker-Metadata @('container','inspect',$container,'--format','{{.Image}}')
        if ($liveImage -cne $images[$service]) { throw "ZEK52_APPLICATION_EXISTING_IMAGE_DRIFT ($service)" }
    }
}

$compose = @('compose','--project-name',$fixture,'--file',(Join-Path $PSScriptRoot 'compose.applications.yaml'))
Docker-Metadata ($compose + @('config','--quiet')) | Out-Null
foreach ($service in @('auth','gateway','frontend','client')) {
    $attempt = @(& docker --context default @compose up -d --no-recreate --no-deps $service 2>&1)
    $native = $LASTEXITCODE
    [ordered]@{service=$service;createExit=$native;image=$images[$service]}|ConvertTo-Json -Compress
    if ($native -ne 0) { throw "ZEK52_APPLICATION_CREATE_FAILED ($service native $native)" }
}
$probe = $fixture + '-frontend-1'
$auth = 'https://auth.mvp02.zeka-m1.test:8443'
$client = 'https://client.mvp02.zeka-m1.test:8443'
$gateway = 'https://gateway.mvp02.zeka-m1.test:8443'
function Wait-PrivateHttps([string]$Url, [string]$HostHeader) {
    $flags = @('exec',$probe,'curl','--noproxy','*','--cacert','/run/secrets/fixture-ca.pem',
        '--max-time','3','--fail','--silent','--output','/dev/null')
    if ($HostHeader) { $flags += @('--header',"Host: $HostHeader") }
    $flags += $Url
    for ($i = 0; $i -lt 15; $i++) {
        $reply = @(& docker --context default @flags 2>&1)
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 1
    }
    throw "ZEK52_PRIVATE_HTTPS_NOT_READY ($Url)"
}

# A completed JWKS response publishes the key before Auth accepts sign-ins.
Wait-PrivateHttps "$auth/.well-known/jwks.json"
Wait-PrivateHttps "$auth/health/ready"
Wait-PrivateHttps "$client/health/ready"
Wait-PrivateHttps "$gateway/health/ready" 'app.mvp02.zeka-m1.test:24446'
$expected = @(Docker-Metadata @('ps','-a','--filter',"label=com.docker.compose.project=$fixture",
    '--format','{{.ID}}|{{.Names}}|{{.State}}|{{.Ports}}'))
$expected | Write-Output
