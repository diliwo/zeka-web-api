[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'AUTH_HTTPS_TEST_OWNER_WINDOWS_REQUIRED' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = [IO.Path]::GetFullPath($Directory)
if ($output -eq $repo -or $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'AUTH_HTTPS_TEST_NEW_OUTSIDE_DIRECTORY_REQUIRED' }
[IO.Directory]::CreateDirectory($output) | Out-Null
function Admit-AuthHttpsTestSource([string]$Path) {
    $scan = & sonar analyze secrets $Path 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or [regex]::IsMatch($scan, '(?i)warning')) { throw 'AUTH_HTTPS_TEST_SOURCE_SCAN_REJECTED' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$initializer = Join-Path $repo 'Deployments/local-stack/Initialize-AuthHttpsConfiguration.ps1'
$template = Join-Path $repo 'Deployments/local-stack/configuration/auth-https.template.json'
$overlay = Join-Path $repo 'Deployments/local-stack/compose.auth-https.yaml'
$compose = Join-Path $repo 'Deployments/local-stack/compose.yaml'
$initializerHash = Admit-AuthHttpsTestSource $initializer
. $initializer -DefinitionsOnly
$templateHash = Admit-AuthHttpsTestSource $template
$overlayHash = Admit-AuthHttpsTestSource $overlay
$composeHash = Admit-AuthHttpsTestSource $compose
$overlayText = Get-Content -LiteralPath $overlay -Raw
$composeText = Get-Content -LiteralPath $compose -Raw
$environmentNames = @('ZEKA_M1_PRIVATE_DIR','ZEKA_M1_AUTH_TLS_CERTIFICATE_SOURCE','ZEKA_M1_AUTH_TLS_KEY_SOURCE','ZEKA_M1_CA_BUNDLE_SOURCE')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$checks = [Collections.Generic.List[string]]::new()
function Require-AuthHttpsTest([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw 'AUTH_HTTPS_TEST_ASSERTION_FAILED' }
    $checks.Add($Name)
}
function New-AuthHttpsTestPrivate([string]$Name, [string]$Missing = '', [string]$San = 'auth.zeka-m1.test',
    [switch]$Expired, [switch]$MismatchedKey, [switch]$UntrustedCa, [switch]$ReusedSigningKey,
    [switch]$WrongApplicationIssuer, [switch]$CaKeyInBundle,
    [switch]$MalformedCertificate, [switch]$MalformedCaBundle) {
    $private = Join-Path $output $Name
    [IO.Directory]::CreateDirectory($private) | Out-Null
    Set-AuthHttpsNewDirectoryAcl $private
    foreach ($part in @('auth','trust','adminarea','client','gateway')) { [IO.Directory]::CreateDirectory((Join-Path $private $part)) | Out-Null }
    $now = [DateTimeOffset]::UtcNow
    $caKey = [Security.Cryptography.RSA]::Create(2048)
    $serverKey = [Security.Cryptography.RSA]::Create(2048)
    $signingKey = [Security.Cryptography.RSA]::Create(2048)
    $ca = $null; $server = $null; $rogueCa = $null; $rogueKey = $null; $wrongKey = $null
    try {
        $caRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Zeka disposable test CA', $caKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $caRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $caRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign', $true))
        $ca = $caRequest.CreateSelfSigned($now.AddDays(-30), $now.AddDays(30))
        $serverRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=auth.zeka-m1.test', $serverKey,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $sanBuilder = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $sanBuilder.AddDnsName($San)
        $serverRequest.CertificateExtensions.Add($sanBuilder.Build())
        $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
        $ekuOids = [Security.Cryptography.OidCollection]::new()
        $null = $ekuOids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $serverRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $true))
        $serial = [byte[]]::new(16)
        [Security.Cryptography.RandomNumberGenerator]::Fill($serial)
        $serial[0] = $serial[0] -band 0x7f
        $notBefore = if ($Expired) { $now.AddDays(-20) } else { $now.AddDays(-1) }
        $notAfter = if ($Expired) { $now.AddDays(-10) } else { $now.AddDays(10) }
        $server = $serverRequest.Create($ca, $notBefore, $notAfter, $serial)
        $bundle = $ca.ExportCertificatePem()
        if ($CaKeyInBundle) { $bundle += $caKey.ExportPkcs8PrivateKeyPem() }
        if ($UntrustedCa) {
            $rogueKey = [Security.Cryptography.RSA]::Create(2048)
            $rogueRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Unrelated disposable CA', $rogueKey,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $rogueRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
            $rogueRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign', $true))
            $rogueCa = $rogueRequest.CreateSelfSigned($now.AddDays(-1), $now.AddDays(10))
            $bundle = $rogueCa.ExportCertificatePem()
        }
        $tlsKeyPem = $serverKey.ExportPkcs8PrivateKeyPem()
        if ($MismatchedKey) {
            $wrongKey = [Security.Cryptography.RSA]::Create(2048)
            $tlsKeyPem = $wrongKey.ExportPkcs8PrivateKeyPem()
        }
        $signingPem = if ($ReusedSigningKey) { $serverKey.ExportPkcs8PrivateKeyPem() } else { $signingKey.ExportPkcs8PrivateKeyPem() }
        $contents = @{
            'auth/tls-certificate.pem' = $server.ExportCertificatePem()
            'auth/tls-key.pem' = $tlsKeyPem
            'auth/signing-key.pem' = $signingPem
            'trust/ca-bundle.pem' = $bundle
        }
        # The nested whitespace quantifiers in the former PEM regex stalled on
        # near-valid delimiters. Keep these fixtures below the 1 MiB size cap.
        if ($MalformedCertificate -or $MalformedCaBundle) {
            $malformed = '-----BEGIN CERTIFICATE-----' + (' ' * 8192) + ('A' * 4096) + '-----END CERTIFICATX-----'
            if ($MalformedCertificate) { $contents['auth/tls-certificate.pem'] = $malformed }
            if ($MalformedCaBundle) { $contents['trust/ca-bundle.pem'] = $malformed }
        }
        foreach ($relative in $contents.Keys) {
            if ($relative -ne $Missing) {
                [IO.File]::WriteAllText((Join-Path $private $relative), $contents[$relative], [Text.UTF8Encoding]::new($false))
            }
        }
        foreach ($service in @('auth','adminarea','client','gateway')) {
            $issuer = if ($WrongApplicationIssuer -and $service -ceq 'client') { 'https://wrong.zeka-m1.test:19443' } else { 'https://auth.zeka-m1.test:19443' }
            $configuration = @{ Authentication=@{ Issuer=$issuer; JwksUri='https://auth.zeka-m1.test:19443/.well-known/jwks.json' } }
            [IO.File]::WriteAllText((Join-Path $private ($service + '/appsettings.LocalStack.json')),
                ($configuration | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        }
    } finally {
        foreach ($resource in @($server, $ca, $rogueCa, $wrongKey, $rogueKey, $serverKey, $signingKey, $caKey)) {
            if ($null -ne $resource) { $resource.Dispose() }
        }
    }
    return $private
}
function Expect-AuthHttpsRefusal([scriptblock]$Action, [string]$Code, [string]$Name) {
    $actual = $null
    try { $null = & $Action } catch { $actual = $_.Exception.Message }
    Require-AuthHttpsTest ($actual -ceq $Code) $Name
}
$source = '0123456789abcdef0123456789abcdef01234567'
try {
    $private = New-AuthHttpsTestPrivate 'valid-private'
    $key = Join-Path $private 'auth/tls-key.pem'
    $keyHash = (Get-FileHash -LiteralPath $key -Algorithm SHA256).Hash
    $prepared = Join-Path $output 'prepared'
    $result = Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory $prepared -SourceCommit $source -Fixture
    $saved = Get-Content -LiteralPath (Join-Path $prepared 'auth-https.environment.json') -Raw | ConvertFrom-Json
    Require-AuthHttpsTest ($result.status -ceq 'CONFIGURATION_PREPARED' -and !$result.applicationReady -and $result.materialContentsRead -and
        !$result.keysGenerated -and $result.trustVerified -and $result.certificateSanAndKeyMatchVerified -and
        $result.signingKeyDistinctVerified -and $result.publicCaBundleOnlyVerified -and $result.applicationAuthoritiesValidated -and
        [Environment]::GetEnvironmentVariable('ZEKA_M1_PRIVATE_DIR', 'Process') -ceq $previousEnvironment['ZEKA_M1_PRIVATE_DIR'] -and
        $result.logicalPrivateDirectory -ceq $private -and
        $result.physicalPrivateDirectory -ceq (Resolve-AuthHttpsPhysicalPath $private) -and
        $saved.ZEKA_M1_PRIVATE_DIR -ceq (Resolve-AuthHttpsPhysicalPath $private) -and
        $saved.ZEKA_M1_AUTH_TLS_KEY_SOURCE -ceq (Resolve-AuthHttpsPhysicalPath $key) -and
        (Get-FileHash -LiteralPath $key -Algorithm SHA256).Hash -ceq $keyHash) 'disposable_valid_material_is_validated_without_runtime_claims'
    Assert-AuthHttpsPrivateMetadata $prepared $true

    $environmentFile = Join-Path $prepared 'auth-https.environment.json'
    $metadataFile = Join-Path $prepared 'auth-https.metadata.json'
    $environmentHash = (Get-FileHash -LiteralPath $environmentFile -Algorithm SHA256).Hash
    $metadataHash = (Get-FileHash -LiteralPath $metadataFile -Algorithm SHA256).Hash
    $rerun = Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory $prepared -SourceCommit $source -Fixture
    Require-AuthHttpsTest ($rerun.status -ceq 'CONFIGURATION_PREPARED' -and
        (Get-FileHash -LiteralPath $environmentFile -Algorithm SHA256).Hash -ceq $environmentHash -and
        (Get-FileHash -LiteralPath $metadataFile -Algorithm SHA256).Hash -ceq $metadataHash) 'matching_protected_output_is_idempotent'

    $originalMetadata = [IO.File]::ReadAllBytes($metadataFile)
    try {
        [IO.File]::WriteAllBytes($metadataFile, ($originalMetadata + [byte[]](10)))
        Expect-AuthHttpsRefusal {
            Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory $prepared -SourceCommit $source -Fixture
        } 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' 'altered_existing_output_is_refused'
    } finally { [IO.File]::WriteAllBytes($metadataFile, $originalMetadata) }
    Require-AuthHttpsTest ((Get-FileHash -LiteralPath $metadataFile -Algorithm SHA256).Hash -ceq $metadataHash) 'tampered_fixture_is_restored'

    $partial = Join-Path $output 'partial-output'
    [IO.Directory]::CreateDirectory($partial) | Out-Null
    Set-AuthHttpsNewDirectoryAcl $partial
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory $partial -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' 'partial_existing_output_is_refused'

    $missing = New-AuthHttpsTestPrivate 'missing-private' 'auth/tls-key.pem'
    $missingOutput = Join-Path $output 'missing-output'
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $missing -OutputDirectory $missingOutput -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_MATERIAL_MISSING' 'missing_material_is_refused_before_output'
    Require-AuthHttpsTest (!(Test-Path -LiteralPath $missingOutput)) 'missing_material_creates_no_output'

    $negativeCases = @(
        @{ name='wrong-san'; options=@{ San='wrong.zeka-m1.test' } },
        @{ name='expired'; options=@{ Expired=$true } },
        @{ name='mismatched-key'; options=@{ MismatchedKey=$true } },
        @{ name='untrusted-ca'; options=@{ UntrustedCa=$true } },
        @{ name='key-reuse'; options=@{ ReusedSigningKey=$true } },
        @{ name='ca-key-in-bundle'; options=@{ CaKeyInBundle=$true } },
        @{ name='malformed-certificate'; options=@{ MalformedCertificate=$true } },
        @{ name='malformed-ca-bundle'; options=@{ MalformedCaBundle=$true } }
    )
    foreach ($case in $negativeCases) {
        $caseOptions = $case.options
        $badPrivate = New-AuthHttpsTestPrivate -Name ($case.name + '-private') @caseOptions
        $badOutput = Join-Path $output ($case.name + '-output')
        Expect-AuthHttpsRefusal {
            Initialize-AuthHttpsConfiguration -PrivateDirectory $badPrivate -OutputDirectory $badOutput -SourceCommit $source -Fixture
        } 'AUTH_HTTPS_CRYPTOGRAPHY_REJECTED' ($case.name + '_material_is_rejected')
        Require-AuthHttpsTest (!(Test-Path -LiteralPath $badOutput)) ($case.name + '_creates_no_output')
    }
    $missingSigning = New-AuthHttpsTestPrivate 'missing-signing-private' 'auth/signing-key.pem'
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $missingSigning -OutputDirectory (Join-Path $output 'missing-signing-output') -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_MATERIAL_MISSING' 'missing_signing_key_is_rejected'
    $wrongApplication = New-AuthHttpsTestPrivate -Name 'wrong-application-issuer-private' -WrongApplicationIssuer
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $wrongApplication -OutputDirectory (Join-Path $output 'wrong-application-issuer-output') -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_APPLICATION_AUTHORITY_REJECTED' 'noncanonical_application_issuer_is_rejected'

    $foreign = New-AuthHttpsTestPrivate 'foreign-acl-private'
    # Load and persist ACCESS only. Do not include owner/group/SACL sections:
    # those can require privileges unavailable to the normal owner process.
    $directoryInfo = [IO.DirectoryInfo]::new($foreign)
    $acl = [IO.FileSystemAclExtensions]::GetAccessControl($directoryInfo, [Security.AccessControl.AccessControlSections]::Access)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-11'),
        [Security.AccessControl.FileSystemRights]::ReadAndExecute, [Security.AccessControl.AccessControlType]::Allow))
    [IO.FileSystemAclExtensions]::SetAccessControl($directoryInfo, $acl)
    $ownerAfter = [IO.FileSystemAclExtensions]::GetAccessControl($directoryInfo, [Security.AccessControl.AccessControlSections]::Owner)
    Require-AuthHttpsTest ($ownerAfter.GetOwner([Security.Principal.SecurityIdentifier]).Value -ceq
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) 'dummy_fixture_current_owner_is_unchanged'
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $foreign -OutputDirectory (Join-Path $output 'foreign-output') -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_PRIVATE_ACL_REJECTED' 'broad_fixture_acl_is_refused_without_repair'

    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory (Join-Path $repo 'tls-test-output-must-not-exist') -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_BUILD_CONTEXT_REJECTED' 'checkout_output_is_refused'

    $junctionTarget = Join-Path $output 'empty-junction-target'
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    $junction = Join-Path $output 'output-parent-junction'
    New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory (Join-Path $junction 'must-not-exist') -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_REPARSE_REJECTED' 'output_parent_junction_is_refused_before_creation'
    Require-AuthHttpsTest (!(Test-Path -LiteralPath (Join-Path $junctionTarget 'must-not-exist'))) 'junction_target_is_unchanged'

    $policy = Read-AuthHttpsSourcePolicy $template
    $policy.issuer = 'http://auth.zeka-m1.test:19443'
    $invalidTemplate = Join-Path $output 'invalid-policy.json'
    Save-AuthHttpsNewJson $invalidTemplate $policy
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory (Join-Path $output 'invalid-policy-output') -SourceCommit $source -Fixture -TemplateFile $invalidTemplate
    } 'AUTH_HTTPS_POLICY_REJECTED' 'noncanonical_or_non_https_authority_is_refused'

    $policy = Read-AuthHttpsSourcePolicy $template
    $policy.verificationBypassAllowed = $true
    $bypassTemplate = Join-Path $output 'verification-bypass-policy.json'
    Save-AuthHttpsNewJson $bypassTemplate $policy
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory (Join-Path $output 'verification-bypass-output') -SourceCommit $source -Fixture -TemplateFile $bypassTemplate
    } 'AUTH_HTTPS_POLICY_REJECTED' 'verification_bypass_is_rejected'

    Require-AuthHttpsTest ($overlayText.Contains('"127.0.0.1:19443:19443"') -and $overlayText.Contains('aliases: [auth.zeka-m1.test]') -and
        $overlayText.Contains('ASPNETCORE_HTTPS_PORTS: "19443"') -and $overlayText.Contains('ASPNETCORE_HTTPS_PORT: "19443"') -and
        $overlayText.Contains('Kestrel__Certificates__Default__Path: /run/secrets/auth_tls_certificate.pem') -and
        $overlayText.Contains('Kestrel__Certificates__Default__KeyPath: /run/secrets/auth_tls_key.pem') -and
        !$overlayText.Contains('ASPNETCORE_HTTP_PORTS') -and !$overlayText.Contains('ASPNETCORE_URLS') -and
        $overlayText -notmatch '(?i)password|allowinvalid|insecure|signing-key|browser-session-hash') 'additive_overlay_preserves_health_and_uses_file_paths_without_bypasses'
    Require-AuthHttpsTest (([regex]::Matches($composeText, 'source: local_ca_bundle')).Count -ge 4 -and
        ([regex]::Matches($composeText, 'SSL_CERT_FILE: /run/secrets/local_ca_bundle.pem')).Count -ge 4 -and
        !$composeText.Contains('/run/secrets/ca-private-key') -and
        $composeText -notmatch '(?i)dangerousacceptanyservercertificate|allowinvalidcertificates|disablecertificateverification') `
        'public_ca_bundle_is_mounted_for_four_application_containers_without_bypass'

    $summary = [ordered]@{
        status='PASS'; assertionsPassed=$checks.Count; assertions=$checks.ToArray()
        syntheticInputsOnly=$true; disposableValidCertificatesCreated=$true; disposablePrivateKeysCreated=$true
        dockerExecuted=$false; trustInstalled=$false; runtimeReadinessEstablished=$false
        ownerChangeTest='NOT RUN; requires privilege; no owner-change assertion is claimed'
        foreignAclTest='unexpected DACL allow rule only; original current-user owner preserved'
        sourceHashes=@{ initializer=$initializerHash; template=$templateHash; overlay=$overlayHash; compose=$composeHash }
    }
    Save-AuthHttpsNewJson (Join-Path $output 'auth-https-tests-result.json') $summary
    $summary | ConvertTo-Json -Depth 6 -Compress
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
