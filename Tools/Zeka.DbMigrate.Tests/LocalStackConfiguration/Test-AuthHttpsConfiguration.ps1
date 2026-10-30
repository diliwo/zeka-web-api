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
    if ($LASTEXITCODE -ne 0 -or !$scan.Contains('No secrets found') -or $scan.Contains('Scan warning:')) { throw 'AUTH_HTTPS_TEST_SOURCE_SCAN_REJECTED' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$initializer = Join-Path $repo 'Deployments/local-stack/Initialize-AuthHttpsConfiguration.ps1'
$template = Join-Path $repo 'Deployments/local-stack/configuration/auth-https.template.json'
$overlay = Join-Path $repo 'Deployments/local-stack/compose.auth-https.yaml'
$initializerHash = Admit-AuthHttpsTestSource $initializer
. $initializer -DefinitionsOnly
$templateHash = Admit-AuthHttpsTestSource $template
$overlayHash = Admit-AuthHttpsTestSource $overlay
$overlayText = Get-Content -LiteralPath $overlay -Raw
$environmentNames = @('ZEKA_M1_PRIVATE_DIR','ZEKA_M1_AUTH_TLS_CERTIFICATE_SOURCE','ZEKA_M1_AUTH_TLS_KEY_SOURCE','ZEKA_M1_CA_BUNDLE_SOURCE')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$checks = [Collections.Generic.List[string]]::new()
function Require-AuthHttpsTest([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw 'AUTH_HTTPS_TEST_ASSERTION_FAILED' }
    $checks.Add($Name)
}
function New-AuthHttpsTestPrivate([string]$Name, [string]$Missing = '') {
    $private = Join-Path $output $Name
    [IO.Directory]::CreateDirectory($private) | Out-Null
    Set-AuthHttpsNewDirectoryAcl $private
    foreach ($relative in @('auth/tls-certificate.pem','auth/tls-key.pem','trust/ca-bundle.pem')) {
        $path = Join-Path $private $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        if ($relative -ne $Missing) {
            [IO.File]::WriteAllText($path, 'NON_SENSITIVE_TLS_FIXTURE_NOT_VALID_KEY_OR_CERTIFICATE', [Text.UTF8Encoding]::new($false))
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
    Require-AuthHttpsTest ($result.status -ceq 'CONFIGURATION_PREPARED' -and !$result.applicationReady -and !$result.materialContentsRead -and
        !$result.keysGenerated -and !$result.trustVerified -and !$result.certificateSanAndKeyMatchVerified -and
        [Environment]::GetEnvironmentVariable('ZEKA_M1_PRIVATE_DIR', 'Process') -ceq $previousEnvironment['ZEKA_M1_PRIVATE_DIR'] -and
        $result.logicalPrivateDirectory -ceq $private -and
        $result.physicalPrivateDirectory -ceq (Resolve-AuthHttpsPhysicalPath $private) -and
        $saved.ZEKA_M1_PRIVATE_DIR -ceq (Resolve-AuthHttpsPhysicalPath $private) -and
        $saved.ZEKA_M1_AUTH_TLS_KEY_SOURCE -ceq (Resolve-AuthHttpsPhysicalPath $key) -and
        (Get-FileHash -LiteralPath $key -Algorithm SHA256).Hash -ceq $keyHash) 'dummy_file_metadata_prepares_only_nonsecret_configuration'
    Assert-AuthHttpsPrivateMetadata $prepared $true

    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $private -OutputDirectory $prepared -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' 'existing_output_is_preserved'

    $missing = New-AuthHttpsTestPrivate 'missing-private' 'auth/tls-key.pem'
    $missingOutput = Join-Path $output 'missing-output'
    Expect-AuthHttpsRefusal {
        Initialize-AuthHttpsConfiguration -PrivateDirectory $missing -OutputDirectory $missingOutput -SourceCommit $source -Fixture
    } 'AUTH_HTTPS_MATERIAL_MISSING' 'missing_material_is_refused_before_output'
    Require-AuthHttpsTest (!(Test-Path -LiteralPath $missingOutput)) 'missing_material_creates_no_output'

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

    Require-AuthHttpsTest ($overlayText.Contains('"127.0.0.1:19443:19443"') -and $overlayText.Contains('aliases: [auth.zeka-m1.test]') -and
        $overlayText.Contains('ASPNETCORE_HTTPS_PORTS: "19443"') -and $overlayText.Contains('ASPNETCORE_HTTPS_PORT: "19443"') -and
        $overlayText.Contains('Kestrel__Certificates__Default__Path: /run/secrets/auth_tls_certificate.pem') -and
        $overlayText.Contains('Kestrel__Certificates__Default__KeyPath: /run/secrets/auth_tls_key.pem') -and
        !$overlayText.Contains('ASPNETCORE_HTTP_PORTS') -and !$overlayText.Contains('ASPNETCORE_URLS') -and
        $overlayText -notmatch '(?i)password|allowinvalid|insecure|signing-key|browser-session-hash') 'additive_overlay_preserves_health_and_uses_file_paths_without_bypasses'

    $summary = [ordered]@{
        status='PASS'; assertionsPassed=$checks.Count; assertions=$checks.ToArray()
        syntheticInputsOnly=$true; validCertificatesCreated=$false; privateKeysCreated=$false
        dockerExecuted=$false; trustInstalled=$false; runtimeReadinessEstablished=$false
        ownerChangeTest='NOT RUN; requires privilege; no owner-change assertion is claimed'
        foreignAclTest='unexpected DACL allow rule only; original current-user owner preserved'
        sourceHashes=@{ initializer=$initializerHash; template=$templateHash; overlay=$overlayHash }
    }
    Save-AuthHttpsNewJson (Join-Path $output 'auth-https-tests-result.json') $summary
    $summary | ConvertTo-Json -Depth 6 -Compress
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
