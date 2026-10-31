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
        if ($process.ExitCode -ne 0 -or [regex]::IsMatch($scan, '(?i)warning')) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_SOURCE_SCAN_REJECTED' }
    } finally { $process.Dispose() }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 12
}
function Assert-AuthHttpsMaterialCryptography([string]$CertificatePath, [string]$KeyPath, [string]$CaBundlePath, [string]$SigningKeyPath) {
    try {
        if (!('ZekaM1AuthHttpsCryptography' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

public static class ZekaM1AuthHttpsCryptography {
    private static X509Certificate2Collection ReadPublicCertificates(string path) {
        if (new FileInfo(path).Length > 1048576) throw new CryptographicException("Certificate bundle too large");
        string pem = File.ReadAllText(path);
        const string pattern = @"-----BEGIN CERTIFICATE-----\s*[A-Za-z0-9+/=\s]+-----END CERTIFICATE-----";
        var blocks = Regex.Matches(pem, pattern, RegexOptions.CultureInvariant);
        if (blocks.Count == 0 || !String.IsNullOrWhiteSpace(Regex.Replace(pem, pattern, "")))
            throw new CryptographicException("Certificate-only PEM required");
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(pem);
        if (certificates.Count != blocks.Count)
            throw new CryptographicException("Invalid certificate bundle");
        return certificates;
    }
    private static bool HasRequiredDnsSan(X509Certificate2 certificate) {
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension == null) return false;
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var names = reader.ReadSequence();
        bool found = false;
        var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
        while (names.HasData) {
            if (names.PeekTag().HasSameClassAndValue(dnsTag)) {
                string dns = names.ReadCharacterString(UniversalTagNumber.IA5String, dnsTag);
                if (String.Equals(dns, "auth.zeka-m1.test", StringComparison.OrdinalIgnoreCase)) found = true;
            } else names.ReadEncodedValue();
        }
        reader.ThrowIfNotEmpty();
        return found;
    }
    private static byte[] PublicKeyFromPrivatePem(string path) {
        if (new FileInfo(path).Length > 16384) throw new CryptographicException("Signing key too large");
        string pem = File.ReadAllText(path);
        using (var rsa = RSA.Create()) {
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048) throw new CryptographicException("RSA signing key too small");
            return rsa.ExportSubjectPublicKeyInfo();
        }
    }
    public static void Validate(string certificatePath, string keyPath, string caBundlePath, string signingKeyPath) {
        if (new FileInfo(keyPath).Length > 16384) throw new CryptographicException("TLS key too large");
        var chainCertificates = ReadPublicCertificates(certificatePath);
        var roots = ReadPublicCertificates(caBundlePath);
        foreach (X509Certificate2 root in roots) {
            var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (constraints == null || !constraints.CertificateAuthority)
                throw new CryptographicException("CA bundle contains a non-CA certificate");
        }
        using (var server = X509Certificate2.CreateFromPemFile(certificatePath, keyPath)) {
            var now = DateTime.UtcNow;
            if (!server.HasPrivateKey || server.NotBefore.ToUniversalTime() > now || server.NotAfter.ToUniversalTime() <= now || !HasRequiredDnsSan(server))
                throw new CryptographicException("TLS certificate invalid");
            var basic = server.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (basic != null && basic.CertificateAuthority) throw new CryptographicException("TLS leaf cannot be a CA");
            var usage = server.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (usage == null || (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0)
                throw new CryptographicException("TLS digital signature usage required");
            var eku = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku == null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.1"))
                throw new CryptographicException("Server authentication EKU required");
            using (var chain = new X509Chain()) {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                foreach (X509Certificate2 intermediate in chainCertificates.Cast<X509Certificate2>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(intermediate);
                if (!chain.Build(server)) throw new CryptographicException("Certificate chain not trusted");
            }
            byte[] tlsPublic;
            using (var rsa = server.GetRSAPublicKey()) {
                if (rsa != null) {
                    if (rsa.KeySize < 2048) throw new CryptographicException("TLS RSA key too small");
                    tlsPublic = rsa.ExportSubjectPublicKeyInfo();
                }
                else {
                    using (var ecdsa = server.GetECDsaPublicKey()) {
                        if (ecdsa == null) throw new CryptographicException("Unsupported TLS key type");
                        if (ecdsa.KeySize < 256) throw new CryptographicException("TLS ECDSA key too small");
                        tlsPublic = ecdsa.ExportSubjectPublicKeyInfo();
                    }
                }
            }
            if (tlsPublic.SequenceEqual(PublicKeyFromPrivatePem(signingKeyPath)))
                throw new CryptographicException("TLS and signing keys must differ");
        }
    }
}
'@
        }
        [ZekaM1AuthHttpsCryptography]::Validate($CertificatePath, $KeyPath, $CaBundlePath, $SigningKeyPath)
    } catch { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_CRYPTOGRAPHY_REJECTED' }
}
function Assert-AuthHttpsApplicationAuthorities([string]$PrivateRoot, [string]$PhysicalRoot) {
    foreach ($service in @('auth','adminarea','client','gateway')) {
        $relative = $service + '/appsettings.LocalStack.json'
        $path = Join-Path $PrivateRoot $relative
        Assert-AuthHttpsPrivateMetadata $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_MATERIAL_MISSING' }
        $physical = Resolve-AuthHttpsPhysicalPath $path
        if (![StringComparer]::OrdinalIgnoreCase.Equals($physical, [IO.Path]::GetFullPath((Join-Path $PhysicalRoot $relative)))) {
            Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED'
        }
        try {
            $configuration = Get-Content -LiteralPath $physical -Raw | ConvertFrom-Json -Depth 12
            if ($configuration.Authentication.Issuer -cne 'https://auth.zeka-m1.test:19443' -or
                $configuration.Authentication.JwksUri -cne 'https://auth.zeka-m1.test:19443/.well-known/jwks.json') {
                Stop-AuthHttpsConfiguration 'AUTH_HTTPS_APPLICATION_AUTHORITY_REJECTED'
            }
        } catch { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_APPLICATION_AUTHORITY_REJECTED' }
    }
}
function Save-AuthHttpsNewJson([string]$Path, $Value) {
    $temporary = Join-Path (Split-Path -Parent $Path) ('.auth-https-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 12))
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary, $Path)
}
function Assert-AuthHttpsExistingOutput([string]$Path, $Environment, $Manifest) {
    Assert-AuthHttpsPrivateMetadata $Path $true
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' }
    $names = @([IO.Directory]::EnumerateFileSystemEntries($Path) | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    if (($names -join ',') -cne 'auth-https.environment.json,auth-https.metadata.json') { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS' }
    foreach ($entry in @(
        @{ Name='auth-https.environment.json'; Value=$Environment },
        @{ Name='auth-https.metadata.json'; Value=$Manifest }
    )) {
        $file = Join-Path $Path $entry.Name
        Assert-AuthHttpsPrivateMetadata $file
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or
            ![StringComparer]::OrdinalIgnoreCase.Equals((Resolve-AuthHttpsPhysicalPath $file), [IO.Path]::GetFullPath($file)) -or
            [IO.File]::ReadAllText($file) -cne ($entry.Value | ConvertTo-Json -Depth 12)) {
            Stop-AuthHttpsConfiguration 'AUTH_HTTPS_OUTPUT_ALREADY_EXISTS'
        }
    }
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
    $policyKeys = @('applicationReady','environment','hostPublication','httpHealthPort','internalDnsAlias','issuer','jwksUri',
        'materials','notPerformed','preserve','project','requiredDnsSan','schemaVersion','signingKeyRelativePath','trustMode','verificationBypassAllowed')
    if ((@($policy.PSObject.Properties.Name | Sort-Object) -join ',') -cne (@($policyKeys | Sort-Object) -join ',')) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
    if ($policy.schemaVersion -cne 'zeka-m1-auth-https-configuration/v1' -or $policy.project -cne 'zeka-m1' -or $policy.environment -cne 'LocalStack' -or
        $policy.issuer -cne 'https://auth.zeka-m1.test:19443' -or $policy.jwksUri -cne 'https://auth.zeka-m1.test:19443/.well-known/jwks.json' -or
        $policy.requiredDnsSan -cne 'auth.zeka-m1.test' -or $policy.internalDnsAlias -cne 'auth.zeka-m1.test' -or
        $policy.hostPublication -cne '127.0.0.1:19443:19443' -or $policy.httpHealthPort -ne 8080 -or
        $policy.applicationReady -isnot [bool] -or $policy.applicationReady -ne $false -or
        $policy.signingKeyRelativePath -cne 'auth/signing-key.pem' -or $policy.trustMode -cne 'CustomRootTrust' -or
        $policy.verificationBypassAllowed -isnot [bool] -or $policy.verificationBypassAllowed -ne $false -or
        @($policy.materials).Count -ne 3) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
    $expected = @{
        'auth/tls-certificate.pem' = @('ZEKA_M1_AUTH_TLS_CERTIFICATE_SOURCE', '/run/secrets/auth_tls_certificate.pem', 'auth')
        'auth/tls-key.pem' = @('ZEKA_M1_AUTH_TLS_KEY_SOURCE', '/run/secrets/auth_tls_key.pem', 'auth')
        'trust/ca-bundle.pem' = @('ZEKA_M1_CA_BUNDLE_SOURCE', '/run/secrets/local_ca_bundle.pem', 'adminarea,auth,client,gateway')
    }
    $environment = [ordered]@{}
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($material in $policy.materials) {
        if ((@($material.PSObject.Properties.Name | Sort-Object) -join ',') -cne 'consumers,containerPath,environmentName,readOnly,relativePath') { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
        $relation = $expected[$material.relativePath]
        if (!$relation -or $expected.Keys -cnotcontains $material.relativePath -or !$seen.Add($material.relativePath) -or
            $material.environmentName -cne $relation[0] -or $material.containerPath -cne $relation[1] -or
            (@($material.consumers | Sort-Object) -join ',') -cne $relation[2] -or
            $material.readOnly -isnot [bool] -or $material.readOnly -ne $true) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_POLICY_REJECTED' }
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
    $signingKey = Join-Path $private $policy.signingKeyRelativePath
    Assert-AuthHttpsPrivateMetadata $signingKey
    if (!(Test-Path -LiteralPath $signingKey -PathType Leaf) -or (Get-Item -LiteralPath $signingKey).Length -le 0) { Stop-AuthHttpsConfiguration 'AUTH_HTTPS_MATERIAL_MISSING' }
    $physicalSigningKey = Resolve-AuthHttpsPhysicalPath $signingKey
    if (![StringComparer]::OrdinalIgnoreCase.Equals($physicalSigningKey, [IO.Path]::GetFullPath((Join-Path $physicalRoot $policy.signingKeyRelativePath)))) {
        Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED'
    }
    Assert-AuthHttpsPrivateMetadata $physicalSigningKey
    Assert-AuthHttpsMaterialCryptography $environment['ZEKA_M1_AUTH_TLS_CERTIFICATE_SOURCE'] $environment['ZEKA_M1_AUTH_TLS_KEY_SOURCE'] `
        $environment['ZEKA_M1_CA_BUNDLE_SOURCE'] $physicalSigningKey
    Assert-AuthHttpsApplicationAuthorities $private $physicalRoot
    # Build both documents before publishing. Existing output is reusable only when
    # it is protected and byte-for-byte equivalent to the current validated inputs.
    $composeEnvironment = [ordered]@{ ZEKA_M1_PRIVATE_DIR=$physicalRoot }
    foreach ($entry in $environment.GetEnumerator()) { $composeEnvironment[$entry.Key] = $entry.Value }
    $manifest = [ordered]@{
        status='CONFIGURATION_PREPARED'; requestedSourceCommit=$SourceCommit
        schemaVersion=$policy.schemaVersion; issuer=$policy.issuer; jwksUri=$policy.jwksUri
        requiredDnsSan=$policy.requiredDnsSan; logicalPrivateDirectory=$private; physicalPrivateDirectory=$physicalRoot
        templateSha256=(Get-FileHash -LiteralPath $TemplateFile -Algorithm SHA256).Hash.ToLowerInvariant()
        materialMetadataValidated=$true; materialContentsRead=$true; runtimeMountReadabilityVerified=$false
        certificateSanAndKeyMatchVerified=$true; trustVerified=$true; signingKeyDistinctVerified=$true
        publicCaBundleOnlyVerified=$true; applicationAuthoritiesValidated=$true; hostsChanged=$false
        keysGenerated=$false; applicationConfigurationVerified=$false; applicationReady=$false
    }
    if (Test-Path -LiteralPath $output) {
        if (![StringComparer]::OrdinalIgnoreCase.Equals((Resolve-AuthHttpsPhysicalPath $output), $output)) {
            Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED'
        }
        Assert-AuthHttpsExistingOutput $output $composeEnvironment $manifest
    } else {
        $staging = Join-Path (Split-Path -Parent $output) ('.auth-https-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($staging) | Out-Null
        Set-AuthHttpsNewDirectoryAcl $staging
        Save-AuthHttpsNewJson (Join-Path $staging 'auth-https.environment.json') $composeEnvironment
        Save-AuthHttpsNewJson (Join-Path $staging 'auth-https.metadata.json') $manifest
        [IO.Directory]::Move($staging, $output)
        if (![StringComparer]::OrdinalIgnoreCase.Equals((Resolve-AuthHttpsPhysicalPath $output), $output)) {
            Stop-AuthHttpsConfiguration 'AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED'
        }
        Assert-AuthHttpsExistingOutput $output $composeEnvironment $manifest
    }
    foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    return [pscustomobject]$manifest
}

if (!$DefinitionsOnly) {
    try { Initialize-AuthHttpsConfiguration -PrivateDirectory $PrivateDirectory -OutputDirectory $OutputDirectory -SourceCommit $SourceCommit -Fixture:$Fixture | ConvertTo-Json -Depth 6 -Compress }
    catch {
        $allowed = @('AUTH_HTTPS_MATERIAL_MISSING','AUTH_HTTPS_REPARSE_REJECTED','AUTH_HTTPS_PRIVATE_ACL_REJECTED','AUTH_HTTPS_PHYSICAL_PATH_UNAVAILABLE',
            'AUTH_HTTPS_SOURCE_SCAN_REJECTED','AUTH_HTTPS_OWNER_WINDOWS_REQUIRED','AUTH_HTTPS_INPUT_REJECTED','AUTH_HTTPS_BUILD_CONTEXT_REJECTED',
            'AUTH_HTTPS_PRIVATE_ROOT_REJECTED','AUTH_HTTPS_OUTPUT_ALREADY_EXISTS','AUTH_HTTPS_POLICY_REJECTED','AUTH_HTTPS_PHYSICAL_IDENTITY_REJECTED',
            'AUTH_HTTPS_CRYPTOGRAPHY_REJECTED','AUTH_HTTPS_APPLICATION_AUTHORITY_REJECTED')
        $code = if ($_.Exception -is [InvalidOperationException] -and $_.Exception.Message -in $allowed) { $_.Exception.Message } else { 'AUTH_HTTPS_CONFIGURATION_REJECTED' }
        [ordered]@{ status='BLOCKED'; code=$code; applicationReady=$false } | ConvertTo-Json -Compress
        exit 1
    }
}
