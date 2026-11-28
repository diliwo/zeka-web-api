#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}$')][string]$FixtureId,
    [ValidateSet('database','database-attempt-2')][string]$DatabaseDirectory = 'database'
)

$ErrorActionPreference = 'Stop'
$project = "zeka-mvp02-$FixtureId"
$root = "D:\Workspace\ZekaLocal\$project"
$settings = Join-Path $root 'settings'
$encoding = [Text.UTF8Encoding]::new($false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = Get-Acl -LiteralPath $root
if ($identity.Name -ine 'DILI_LP\herve' -or !$acl.AreAccessRulesProtected -or
    $acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $identity.User -or
    ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ZEK52_SETTINGS_PRIVATE_ROOT_REJECTED'
}
if (Test-Path -LiteralPath $settings) { throw 'ZEK52_SETTINGS_ALREADY_EXIST' }

$authHost = 'auth.mvp02.zeka-m1.test'
$clientHost = 'client.mvp02.zeka-m1.test'
$gatewayHost = 'gateway.mvp02.zeka-m1.test'
$browserOrigin = 'https://app.mvp02.zeka-m1.test:24446'
$issuer = "https://${authHost}:8443"

function Read-ScannedJson([string]$Path) {
    $scan = @(sonar analyze secrets $Path 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'ZEK52_PUBLIC_CONFIGURATION_SCAN_REJECTED' }
    return ConvertFrom-Json -AsHashtable -InputObject ([IO.File]::ReadAllText($Path))
}
function Read-ProtectedFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $item.FullName.EndsWith('.log')) { throw 'ZEK52_SETTINGS_FILE_TYPE_REJECTED' }
    return [IO.File]::ReadAllText($item.FullName)
}
function Write-Settings([string]$Name, $Value) {
    $path = Join-Path $settings $Name
    if (Test-Path -LiteralPath $path) { throw 'ZEK52_SETTINGS_OVERWRITE_REJECTED' }
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 12), $encoding)
}
function New-AppSettings([string[]]$Hosts) {
    return @{
        AllowedHosts = $Hosts -join ';'
        Logging = @{LogLevel=@{Default='Warning'; 'Microsoft.EntityFrameworkCore'='Error'}}
        Kestrel = @{Endpoints=@{Https=@{
            Url='https://0.0.0.0:8443'
            Certificate=@{Path='/run/secrets/service.pem';KeyPath='/run/secrets/service.key'}
        }}}
        Authentication = $authentication
    }
}

$infrastructure = Read-ScannedJson (Join-Path $root 'infrastructure.json')
$publicIdentity = Read-ScannedJson (Join-Path $root 'tls\public-identity.json')
if ($infrastructure.fixtureId -cne $FixtureId -or $infrastructure.project -cne $project -or
    $publicIdentity.fixtureId -cne $FixtureId -or $publicIdentity.signingKeyId -cnotmatch '^[A-Za-z0-9_-]{43}$') {
    throw 'ZEK52_SETTINGS_FIXTURE_IDENTITY_REJECTED'
}
$routes = Read-ScannedJson (Join-Path (Split-Path $PSScriptRoot -Parent) 'ocelot.json')
if (@($routes.Routes).Count -ne 11) { throw 'ZEK52_GATEWAY_AUTH_ROUTE_SNAPSHOT_REJECTED' }
$authDatabase = ConvertFrom-Json -AsHashtable -InputObject (Read-ProtectedFile (Join-Path $root "$DatabaseDirectory\auth\database.LocalStack.json"))
$clientDatabase = ConvertFrom-Json -AsHashtable -InputObject (Read-ProtectedFile (Join-Path $root "$DatabaseDirectory\client\database.LocalStack.json"))
if ($authDatabase.ConnectionStrings.Default -cnotmatch '^Host=postgres;Port=5432;Database=zeka_m1_auth;Username=zeka_auth_runtime;' -or
    $clientDatabase.ConnectionStrings.ClientApiConnection -cnotmatch '^Host=postgres;Port=5432;Database=zeka_m1_client;Username=zeka_client_runtime;') {
    throw 'ZEK52_SETTINGS_DATABASE_IDENTITY_REJECTED'
}

$brokerPassword = Read-ProtectedFile (Join-Path $root 'broker-password')
if ($brokerPassword -cnotmatch '^[A-F0-9]{64}$') { throw 'ZEK52_SETTINGS_BROKER_IDENTITY_REJECTED' }
$authentication = @{
    Issuer=$issuer; Audience='zeka-mvp02'; JwksUri="$issuer/.well-known/jwks.json"
    ClockSkew='00:00:30'; JwksRefreshInterval='00:01:00'; JwksMaximumStaleness='00:05:00'
}

[IO.Directory]::CreateDirectory($settings) | Out-Null
foreach ($keyName in @('session-hash.key', 'registration-hmac.key')) {
    $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    try { [IO.File]::WriteAllBytes((Join-Path $settings $keyName), $bytes) }
    finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes) }
}
$auth = New-AppSettings @($authHost, 'app.mvp02.zeka-m1.test')
$auth.ConnectionStrings = $authDatabase.ConnectionStrings
$auth.AuthenticationIssuer = @{
    ActiveSigningKeyReference='/run/secrets/auth_signing_key.pem'
    ActiveKeyId=$publicIdentity.signingKeyId
    AccessTokenLifetime='00:30:00'
}
$auth.BrowserSession = @{
    Origin=$browserOrigin; HashKeyReference='/run/secrets/session-hash.key'
    AccessLifetime='00:30:00'; IdleLifetime='01:00:00'; AbsoluteLifetime='04:00:00'
    TrustedProxyPeers=@('10.248.52.6')
    Limits=@{PerAddress=30; Aggregate=80; WindowSeconds=60; MaxPartitions=128}
}
$auth.OnboardingRegistration = @{
    Enabled=$true; VerificationMailpitEnabled=$true; Deployment=$project
    VerificationPagePath='/verify-email'; ActiveKeyId='mvp02'
    VerificationKeyFiles=@{mvp02='/run/secrets/registration-hmac.key'}
}
$auth.LocalM1OrganisationCreation = @{Enabled=$true; MaxOwnedOrganisations=1}
Write-Settings 'auth.json' $auth

$client = New-AppSettings @($clientHost)
$client.ConnectionStrings = $clientDatabase.ConnectionStrings
$client.TenantAuthorization = @{AuthManagementUrl=$issuer}
$client.FileServerPath = '/tmp/zeka-files'
$client.RabbitMq = @{HostName='rabbitmq'; Username='mvp02-local'; Password=$brokerPassword; ManagementEndpoint='http://rabbitmq:15672'}
$client.EventBus = @{QueueName='zeka-m1-client-v130'}
Write-Settings 'client.json' $client

$gateway = New-AppSettings @($gatewayHost, 'app.mvp02.zeka-m1.test')
$gateway.GatewayBrowserTransport = @{
    Origin=$browserOrigin; DownstreamOrigin=$issuer
    ClientDownstreamOrigin="https://${clientHost}:8443"; ClientPeers=@('10.248.52.10')
}
Write-Settings 'gateway.json' $gateway
Write-Settings 'ocelot.json' $routes
Write-Settings 'frontend.json' @{ApiBaseUrl=$browserOrigin}
Write-Output 'ZEK52_LOCAL_APPLICATION_SETTINGS_PREPARED'
