#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{8}$')][string]$FixtureId)

$ErrorActionPreference = 'Stop'
$project = "zeka-mvp02-$FixtureId"
$privateRoot = "D:\Workspace\ZekaLocal\$project"
$tls = Join-Path $privateRoot 'tls'
$statePath = Join-Path $privateRoot 'infrastructure.json'
$openssl = 'C:\Program Files\Git\usr\bin\openssl.exe'
$encoding = [Text.UTF8Encoding]::new($false)

function Invoke-OpenSsl([string]$Stage, [string[]]$Arguments) {
    $sealed = @(& $openssl @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "ZEK52_TLS_$Stage (native $LASTEXITCODE)" }
}
function Write-ExtensionFile([string]$Name, [string[]]$Lines) {
    $p = Join-Path $tls $Name
    [IO.File]::WriteAllText($p, ($Lines -join [char]10) + [char]10, $encoding)
    return $p
}

$owner = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = Get-Acl -LiteralPath $privateRoot
if ($owner.Name -ine 'DILI_LP\herve' -or !$acl.AreAccessRulesProtected -or
    $acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner.User -or
    ((Get-Item -LiteralPath $privateRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'ZEK52_TLS_PRIVATE_ROOT_REJECTED'
}
if ((Test-Path -LiteralPath $tls) -or -not (Test-Path -LiteralPath $openssl -PathType Leaf)) { throw 'ZEK52_TLS_PREFLIGHT_REJECTED' }
$scan = @(sonar analyze secrets $statePath 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'ZEK52_TLS_FIXTURE_SCAN_REJECTED' }
$state = ConvertFrom-Json -AsHashtable -InputObject (Get-Content -LiteralPath $statePath -Raw)
if ($state.fixtureId -cne $FixtureId -or $state.project -cne $project -or
    $state.engineId -cne '0b62f5ec-15d9-4e4b-a450-817d52378eef') { throw 'ZEK52_TLS_FIXTURE_IDENTITY_REJECTED' }
[IO.Directory]::CreateDirectory($tls) | Out-Null

$zone = 'mvp02.zeka-m1.test'
$rootCertificate = Join-Path $tls 'ca.pem'
$rootKey = Join-Path $tls 'ca.key'
$rootConfiguration = Write-ExtensionFile 'ca.cnf' @(
    '[req]', 'distinguished_name=dn', 'prompt=no', 'x509_extensions=ca',
    '[dn]', "CN=Zeka MVP02 $FixtureId Fixture CA", '[ca]',
    'basicConstraints=critical,CA:true,pathlen:0', 'keyUsage=critical,keyCertSign,cRLSign',
    'subjectKeyIdentifier=hash', "nameConstraints=critical,permitted;DNS:.$zone"
)
Invoke-OpenSsl 'CREATE_CA_FAILED' @('req','-new','-x509','-newkey','rsa:3072','-nodes','-sha256','-days','14',
    '-config',$rootConfiguration,'-keyout',$rootKey,'-out',$rootCertificate)

$certificates = [Collections.Generic.List[object]]::new()
foreach ($service in @('auth', 'client', 'gateway', 'app')) {
    $name = "$service.$zone"
    $leafKey = Join-Path $tls "$service.key"
    $leafCertificate = Join-Path $tls "$service.pem"
    $request = Join-Path $tls "$service.csr"
    $extensions = Write-ExtensionFile "$service.cnf" @(
        'basicConstraints=critical,CA:false', 'keyUsage=critical,digitalSignature,keyEncipherment',
        'extendedKeyUsage=serverAuth', 'subjectKeyIdentifier=hash', 'authorityKeyIdentifier=keyid:always',
        "subjectAltName=DNS:$name"
    )
    Invoke-OpenSsl 'CREATE_LEAF_REQUEST_FAILED' @('req','-new','-newkey','rsa:2048','-nodes','-sha256',
        '-subj',"/CN=$name",'-keyout',$leafKey,'-out',$request)
    $serial = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    Invoke-OpenSsl 'ISSUE_LEAF_FAILED' @('x509','-req','-in',$request,'-CA',$rootCertificate,'-CAkey',$rootKey,
        '-set_serial',"0x$serial",'-out',$leafCertificate,'-days','7','-sha256','-extfile',$extensions)
    Invoke-OpenSsl 'VERIFY_LEAF_FAILED' @('verify','-x509_strict','-CAfile',$rootCertificate,'-purpose','sslserver',
        '-verify_hostname',$name,$leafCertificate)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($leafCertificate,$leafKey)
    try {
        $certificates.Add([ordered]@{
            name = $name; expiresAtUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
            certificateSha256 = $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
        })
    } finally { $certificate.Dispose() }
}

$activeSigningKey = Join-Path $tls 'auth_signing_key.pem'
Invoke-OpenSsl 'CREATE_DISPOSABLE_SIGNING_KEY_FAILED' @('genpkey','-algorithm','RSA',
    '-pkeyopt','rsa_keygen_bits:3072','-out',$activeSigningKey)
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($activeSigningKey))
    $public = $rsa.ExportParameters($false)
    $e = [Convert]::ToBase64String($public.Exponent).TrimEnd('=').Replace('+','-').Replace('/','_')
    $n = [Convert]::ToBase64String($public.Modulus).TrimEnd('=').Replace('+','-').Replace('/','_')
    $jwk = [ordered]@{e=$e; kty='RSA'; n=$n} | ConvertTo-Json -Compress
    $digest = [Security.Cryptography.SHA256]::HashData($encoding.GetBytes($jwk))
    $kid = [Convert]::ToBase64String($digest).TrimEnd('=').Replace('+','-').Replace('/','_')
} finally { $rsa.Dispose() }

$root = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem([IO.File]::ReadAllText($rootCertificate))
try {
    $receipt = [ordered]@{
        fixtureId=$FixtureId; caName=$root.Subject; caExpiresAtUtc=$root.NotAfter.ToUniversalTime().ToString('o')
        caCertificateSha256=$root.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
        serverCertificates=$certificates.ToArray(); signingKeyId=$kid
    }
    $doc = $receipt | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $tls 'public-identity.json'),$doc,$encoding)
    Write-Output $doc
} finally { $root.Dispose() }
