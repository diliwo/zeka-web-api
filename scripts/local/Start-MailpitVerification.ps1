#requires -Version 7.0
<##
Starts the already configured local verification slice. It does not provision a
database, change privileges, issue credentials, alter trust, or rebuild images.
#>
[CmdletBinding()]
param(
    [string]$ComposePath = 'D:\Workspace\Zeka49Runtime\compose.zek49.json'
)

$ErrorActionPreference = 'Stop'

function Invoke-LocalDocker([string[]]$Arguments, [int]$TimeoutSeconds = 30) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command docker -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('--context', 'default') + $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw 'LOCAL_DOCKER_OPERATION_TIMEOUT'
        }
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $stdout.GetAwaiter().GetResult()
            Error = $stderr.GetAwaiter().GetResult()
        }
    } finally { $process.Dispose() }
}

function Assert-LocalDocker($Result, [string]$Stage) {
    if ($Result.ExitCode -ne 0) {
        Write-Verbose $Result.Error
        throw ($Stage + ': native exit ' + $Result.ExitCode)
    }
}

if (!(Test-Path -LiteralPath $ComposePath -PathType Leaf)) { throw 'LOCAL_SLICE_COMPOSE_MISSING' }
$mailpit = Invoke-LocalDocker @('inspect', '--format', '{{.State.Running}}', 'mailpit')
Assert-LocalDocker $mailpit 'MAILPIT_INSPECTION'
if ($mailpit.Output.Trim() -cne 'true') { throw 'OWNER_MANAGED_MAILPIT_NOT_RUNNING' }

# Start only the existing database and Auth; leave owner-managed Mailpit alone.
foreach ($name in 'zeka-m1-bootstrap-fixture-4070192a-postgres-1', 'zeka-local-auth') {
    $result = Invoke-LocalDocker @('start', $name)
    Assert-LocalDocker $result ('START_' + $name)
}
$result = Invoke-LocalDocker @('compose', '--project-name', 'zeka-local', '--file', $ComposePath,
    'up', '-d', '--no-build', '--no-deps', 'gateway', 'frontend') 60
Assert-LocalDocker $result 'LOCAL_SLICE_START'

# The established issuer refuses issuance until its actual HTTPS JWKS has been
# published. Probe that public endpoint, rather than bypassing the publication guard.
$probe = @'
printf 'GET /.well-known/jwks.json HTTP/1.1\r\nHost: auth.discovery-4070192a.zeka-m1.test:24445\r\nConnection: close\r\n\r\n' | timeout 10 openssl s_client -connect 127.0.0.1:8443 -servername auth.discovery-4070192a.zeka-m1.test -CAfile /run/secrets/local_ca_bundle.pem -verify_hostname auth.discovery-4070192a.zeka-m1.test -verify_return_error -quiet
'@
$published = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $result = Invoke-LocalDocker @('exec', 'zeka-local-auth', '/bin/sh', '-c', $probe) 15
    $parts = $result.Output.Replace("`r`n", "`n").Split("`n`n", 2)
    if ($parts.Count -eq 2 -and $parts[0] -match '^HTTP/1\.1 200 ' -and
        $parts[0] -match '(?im)^Content-Length: (\d+)$') {
        $length = [int]$Matches[1]
        $body = $parts[1].TrimEnd("`n")
        $keys = $null
        try { $keys = (ConvertFrom-Json -InputObject $body -AsHashtable).keys } catch { }
        $uncleanClose = $result.Error -match 'unexpected eof while reading'
        if ([Text.Encoding]::UTF8.GetByteCount($body) -eq $length -and $keys.Count -gt 0 -and
            $result.Error -match 'verify return:1' -and
            ($result.ExitCode -eq 0 -or ($result.ExitCode -eq 1 -and $uncleanClose))) {
            $published = $true
            if ($uncleanClose) {
                Write-Warning 'JWKS HTTP 200 and certificate verification succeeded; the existing OpenSSL unclean-close result remains recorded.'
            }
            break
        }
    }
    Start-Sleep -Seconds 1
}
if (!$published) { throw 'AUTH_JWKS_PUBLICATION_NOT_VERIFIED' }

# Normal Windows HTTPS validation: no custom certificate callback or trust change.
$handler = [Net.Http.HttpClientHandler]::new()
$handler.UseProxy = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)
try {
    foreach ($path in '/health/live', '/health/ready') {
        $response = $client.GetAsync('https://gateway.discovery-4070192a.zeka-m1.test:24446' + $path).GetAwaiter().GetResult()
        try { if ([int]$response.StatusCode -ne 200) { throw ('GATEWAY_NOT_READY: ' + $path) } }
        finally { $response.Dispose() }
    }
} finally { $client.Dispose(); $handler.Dispose() }

Write-Output 'Registration: https://gateway.discovery-4070192a.zeka-m1.test:24446/register'
Write-Output 'Mailpit:      http://127.0.0.1:18025'
