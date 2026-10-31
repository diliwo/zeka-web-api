[CmdletBinding()]
param(
    [string]$PrivateDirectory = $env:ZEKA_M1_PRIVATE_DIR,
    [string]$OutputDirectory,
    [string]$SourceCommit,
    [switch]$Fixture,
    [switch]$DefinitionsOnly
)

function Stop-AuthHttpsConfiguration([string]$Code) { throw [InvalidOperationException]::new($Code) }
function Assert-AuthHttpsPrivateMetadata([string]$Path, [bool]$RequireProtectedDirectory = $false) {
    if (!(Test-Path -LiteralPath $Path)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_MATERIAL_MISSING' }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_REPARSE_REJECTED' }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $allowed = @($owner.Value, 'S-1-5-18', 'S-1-5-32-544') | Sort-Object -Unique
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $owner.Value -or $rules.Count -ne $allowed.Count -or
        @($rules | Where-Object { $_.IdentityReference.Value -notin $allowed -or $_.AccessControlType -ne 'Allow' -or
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl }).Count -ne 0 -or
        ($RequireProtectedDirectory -and (!$item.PSIsContainer -or !$acl.AreAccessRulesProtected))) {
        Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PRIVATE_ACL_REJECTED'
    }
}
function Set-AuthHttpsNewDirectoryAcl([string]$Path) {
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($owner)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($owner.Value, 'S-1-5-18', 'S-1-5-32-544') | Sort-Object -Unique) {
        $sid = [Security.Principal.SecurityIdentifier]::new($identity)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-AuthHttpsPrivateMetadata $Path $true
}
function Resolve-AuthHttpsPhysicalPath([string]$Path) {
    if (!('ZekaM1AuthHttpsMetadataPath' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ZekaM1AuthHttpsMetadataPath {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    public static string Resolve(string path) {
        // FILE_READ_ATTRIBUTES only: this handle cannot read certificate/key contents.
        using (var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new IOException();
            var result = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
            if (length == 0 || length >= result.Capacity) throw new IOException();
            var value = result.ToString();
            if (!value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return value.Substring(4);
        }
    }
}
"@
    }
    try { return [ZekaM1AuthHttpsMetadataPath]::Resolve($Path) }
    catch { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_PATH_UNAVAILABLE' }
}
function Assert-AuthHttpsNoAncestorReparse([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            Stop-AuthHttpsConfiguration 'AUTH_HTTPS_REPARSE_REJECTED'
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Assert-AuthHttpsOutsideSource([string]$Path, [string]$Repo) {
    $separator = [IO.Path]::DirectorySeparatorChar
    if ([StringComparer]::OrdinalIgnoreCase.Equals($Path, $Repo) -or
        $Path.StartsWith($Repo.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $Repo.StartsWith($Path.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-AuthHttpsConfiguration 'AUTH_HTTPS_BUILD_CONTEXT_REJECTED'
    }
}
function Resolve-AuthHttpsNewOutput([string]$Path, [string]$PhysicalRepo) {
    Assert-AuthHttpsNoAncestorReparse $Path
    $ancestor = Split-Path -Parent $Path
    while ($ancestor -and !(Test-Path -LiteralPath $ancestor)) { $ancestor = Split-Path -Parent $ancestor }
    if (!$ancestor -or !(Test-Path -LiteralPath $ancestor -PathType Container)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_PATH_UNAVAILABLE' }
    $physicalAncestor = Resolve-AuthHttpsPhysicalPath $ancestor
    $relative = [IO.Path]::GetRelativePath($ancestor, $Path)
    if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_PATH_UNAVAILABLE' }
    $physical = [IO.Path]::GetFullPath((Join-Path $physicalAncestor $relative))
    Assert-AuthHttpsOutsideSource $physical $PhysicalRepo
    return $physical
}
function Read-AuthHttpsSourcePolicy([string]$Path) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command sonar -ErrorAction Stop).Source)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['LOG_LEVEL'] = 'SILENT'
    foreach ($argument in @('analyze', 'secrets', $Path)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill($true); $null = $process.WaitForExit(5000); Stop-AuthHttpsConfiguration 'AUTH_HTTPS_SOURCE_SCAN_REJECTED' }
        if (!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))).Wait(5000)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_SOURCE_SCAN_REJECTED' }
        $scan = $stdout.Result + $stderr.Result
        if ($process.ExitCode -ne 0 -or !$scan.Contains('No secrets found') -or $scan.Contains('Scan warning:')) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_SOURCE_SCAN_REJECTED' }
    } finally { $process.Dispose() }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 12
}
function Save-AuthHttpsNewJson([string]$Path, $Value) {
    $temporary = Join-Path (Split-Path -Parent $Path) ('.auth-https-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $Path)
}
function Initialize-AuthHttpsConfiguration {
    [CmdletBinding()]
    param([string]$PrivateDirectory, [string]$OutputDirectory, [string]$SourceCommit, [switch]$Fixture, [string]$TemplateFile)
    $ErrorActionPreference = 'Stop'
    if (!$IsWindows) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_OWNER_WINDOWS_REQUIRED' }
    if ($SourceCommit -cnotmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($PrivateDirectory) -or [string]::IsNullOrWhiteSpace($OutputDirectory)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_INPUT_REJECTED' }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $private = [IO.Path]::GetFullPath($PrivateDirectory)
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $comparison = [StringComparison]::OrdinalIgnoreCase
    if ($private -eq $repo -or $private.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, $comparison) -or
        $repo.StartsWith($private.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, $comparison) -or
        $output -eq $repo -or $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, $comparison)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_BUILD_CONTEXT_REJECTED' }
    $ownerRoot = 'C:\Users\herve\AppData\Local\Zeka\M1\private'
    if ((!$Fixture -and ![StringComparer]::OrdinalIgnoreCase.Equals($private, $ownerRoot)) -or
        ($Fixture -and [StringComparer]::OrdinalIgnoreCase.Equals($private, $ownerRoot))) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PRIVATE_ROOT_REJECTED' }
    if (Test-Path -LiteralPath $output) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' }
    Assert-AuthHttpsNoAncestorReparse $private
    Assert-AuthHttpsPrivateMetadata $private $true
    $physicalRepo = Resolve-AuthHttpsPhysicalPath $repo
    $physicalRoot = Resolve-AuthHttpsPhysicalPath $private
    Assert-AuthHttpsOutsideSource $physicalRoot $physicalRepo
    Assert-AuthHttpsPrivateMetadata $physicalRoot $true
    $output = Resolve-AuthHttpsNewOutput $output $physicalRepo
    if (!$TemplateFile) { $TemplateFile = Join-Path $PSScriptRoot 'configuration/auth-https.template.json' }
    elseif (!$Fixture) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
    $policy = Read-AuthHttpsSourcePolicy $TemplateFile
    if ($policy.schemaVersion -cne 'zeka-m1-auth-https-configuration/v1' -or $policy.project -cne 'zeka-m1' -or $policy.environment -cne 'LocalStack' -or
        $policy.issuer -cne 'https://auth.zeka-m1.test:19443' -or $policy.jwksUri -cne 'https://auth.zeka-m1.test:19443/.well-known/jwks.json' -or
        $policy.requiredDnsSan -cne 'auth.zeka-m1.test' -or $policy.internalDnsAlias -cne 'auth.zeka-m1.test' -or
        $policy.hostPublication -cne '127.0.0.1:19443:19443' -or $policy.httpHealthPort -ne 8080 -or $policy.applicationReady -ne $false -or @($policy.materials).Count -ne 3) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
    $expected = @{
        'auth/tls-certificate.pem' = @('ZEKA_M1_AUTH_TLS_CERTIFICATE_SOURCE', '/run/secrets/auth_tls_certificate.pem', 'auth')
        'auth/tls-key.pem' = @('ZEKA_M1_AUTH_TLS_KEY_SOURCE', '/run/secrets/auth_tls_key.pem', 'auth')
        'trust/ca-bundle.pem' = @('ZEKA_M1_CA_BUNDLE_SOURCE', '/run/secrets/local_ca_bundle.pem', 'adminarea,auth,client,gateway')
    }
    $environment = [ordered]@{}
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($material in $policy.materials) {
        $relation = $expected[$material.relativePath]
        if (!$relation -or !$seen.Add($material.relativePath) -or $material.environmentName -cne $relation[0] -or $material.containerPath -cne $relation[1] -or
            (@($material.consumers | Sort-Object) -join ',') -cne $relation[2] -or $material.readOnly -ne $true) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
        $path = Join-Path $private $material.relativePath
        Assert-AuthHttpsPrivateMetadata (Split-Path -Parent $path)
        Assert-AuthHttpsPrivateMetadata $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_MATERIAL_MISSING' }
        $physical = Resolve-AuthHttpsPhysicalPath $path
        $expectedPhysical = [IO.Path]::GetFullPath((Join-Path $physicalRoot $material.relativePath))
        if (![StringComparer]::OrdinalIgnoreCase.Equals($physical, $expectedPhysical)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED' }
        Assert-AuthHttpsPrivateMetadata $physical
        $environment[$material.environmentName] = $physical
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $createdOutput = Resolve-AuthHttpsPhysicalPath $output
    Assert-AuthHttpsOutsideSource $createdOutput $physicalRepo
    if (![StringComparer]::OrdinalIgnoreCase.Equals($output, $createdOutput)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED' }
    Set-AuthHttpsNewDirectoryAcl $output
    # The common logical root belongs to the DB contract. This physical override
    # is only a proposed environment for a future isolated Compose child.
    $composeEnvironment = [ordered]@{ ZEKA_M1_PRIVATE_DIR=$physicalRoot }
    foreach ($entry in $environment.GetEnumerator()) { $composeEnvironment[$entry.Key] = $entry.Value }
    Save-AuthHttpsNewJson (Join-Path $output 'auth-https.environment.json') $composeEnvironment
    $manifest = [ordered]@{
        status='CONFIGURATION_PREPARED'; requestedSourceCommit=$SourceCommit
        schemaVersion=$policy.schemaVersion; issuer=$policy.issuer; jwksUri=$policy.jwksUri
        requiredDnsSan=$policy.requiredDnsSan; logicalPrivateDirectory=$private; physicalPrivateDirectory=$physicalRoot
        templateSha256=(Get-FileHash -LiteralPath $TemplateFile -Algorithm SHA256).Hash.ToLowerInvariant()
        materialMetadataValidated=$true; materialContentsRead=$false; runtimeMountReadabilityVerified=$false
        certificateSanAndKeyMatchVerified=$false; trustVerified=$false; hostsChanged=$false
        keysGenerated=$false; applicationConfigurationVerified=$false; applicationReady=$false
    }
    Save-AuthHttpsNewJson (Join-Path $output 'auth-https.metadata.json') $manifest
    foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    return [pscustomobject]$manifest
}

if (!$DefinitionsOnly) {
    try { Initialize-AuthHttpsConfiguration -PrivateDirectory $PrivateDirectory -OutputDirectory $OutputDirectory -SourceCommit $SourceCommit -Fixture:$Fixture | ConvertTo-Json -Depth 6 -Compress }
    catch {
        $allowed = @('AUTH_HTTPS_MATERIAL_MISSING','AUTH_HTTPS_REPARSE_REJECTED','AUTH_HTTPS_PRIVATE_ACL_REJECTED','AUTH_HTTPS_PHYSICAL_PATH_UNAVAILABLE',
            'AUTH_HTTPS_SOURCE_SCAN_REJECTED','AUTH_HTTPS_OWNER_WINDOWS_REQUIRED','AUTH_HTTPS_INPUT_REJECTED','AUTH_HTTPS_BUILD_CONTEXT_REJECTED',
            'AUTH_HTTPS_PRIVATE_ROOT_REJECTED','AUTH_HTTPS_OUTPUT_ALREADY_EXISTS','AUTH_HTTPS_POLICY_REJECTED','AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED')
        $code = if ($_.Exception -is [InvalidOperationException] -and $_.Exception.Message -in $allowed) { $_.Exception.Message } else { 'AUTH_HTTPS_CONFIGURATION_REJECTED' }
        [ordered]@{ status='BLOCKED'; code=$code; applicationReady=$false } | ConvertTo-Json -Compress
        exit 1
    }
}
