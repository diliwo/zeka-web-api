# Narrow native diagnostics for the owner-local M1 database launchers.
# Secret values stay in memory; no command line or environment value inventory is persisted.
$script:W3Secrets = [Collections.Generic.List[string]]::new()
$script:W3DiagnosticContext = $null

function Initialize-W3Diagnostics {
    param([Parameter(Mandatory)][string]$Directory,
          [Parameter(Mandatory)][string]$FixtureId,
          [Parameter(Mandatory)][string]$EngineId)
    $full = [IO.Path]::GetFullPath($Directory)
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if ([StringComparer]::OrdinalIgnoreCase.Equals($full, $repo) -or
        $full.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw [InvalidOperationException]::new('DIAGNOSTICS_INSIDE_CHECKOUT_REJECTED')
    }
    if (($FixtureId -ne 'zeka-m1' -and $FixtureId -notmatch '^[A-Za-z0-9_-]{8,128}$') -or
        $EngineId -notmatch '^[A-Za-z0-9_-]{8,128}$') {
        throw [InvalidOperationException]::new('DIAGNOSTIC_IDENTITY_REJECTED')
    }
    [IO.Directory]::CreateDirectory($full) | Out-Null
    $parent = Get-Item -LiteralPath $full
    while ($parent) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw [InvalidOperationException]::new('DIAGNOSTIC_REPARSE_REJECTED')
        }
        $parent = $parent.Parent
    }
    $journal = Join-Path $full ('native-' + [Guid]::NewGuid().ToString('N') + '.ndjson')
    $stream = [IO.FileStream]::new($journal,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $stream.Dispose()
    $script:W3DiagnosticContext = @{ Journal=$journal; FixtureId=$FixtureId; EngineId=$EngineId }
    return $journal
}

function Register-W3Secret {
    param([Parameter(Mandatory)][string]$Value)
    if ($Value.Length -gt 0 -and !$script:W3Secrets.Contains($Value)) {
        $script:W3Secrets.Add($Value)
    }
}

function ConvertTo-W3SafeText {
    param([AllowNull()][AllowEmptyString()][string]$Text)
    if ($null -eq $Text) { return '' }
    $safe = $Text
    foreach ($secret in $script:W3Secrets) {
        $safe = $safe.Replace($secret,'[REDACTED]',[StringComparison]::Ordinal)
    }
    # Redact specific credential fields, not identifiers, hashes, paths or whole errors.
    $field = '(?i)(\b(?:password|passwd|pwd|token|secret|access_token|refresh_token|client_secret|api_key|bearer_token|connectionstring)\b["'']?\s*[:=]\s*)(?:"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|[^\s;,\r\n}]+)'
    $safe = [regex]::Replace($safe,$field,'$1[REDACTED]')
    $safe = [regex]::Replace($safe,'(?i)(\bAuthorization\s*:\s*Bearer\s+)[^\s,\r\n]+','$1[REDACTED]')
    $safe = [regex]::Replace($safe,'(?i)([a-z][a-z0-9+.-]*://[^/\s:@]+:)[^/\s@]+@','$1[REDACTED]@')
    return $safe
}

function Write-W3DiagnosticRecord {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Record)
    if (!$script:W3DiagnosticContext) {
        throw [InvalidOperationException]::new('DIAGNOSTICS_NOT_INITIALIZED')
    }
    $base = [ordered]@{
        schema='zeka-m1-native-diagnostic/v1'
        utc=[DateTime]::UtcNow.ToString('o')
        fixtureId=$script:W3DiagnosticContext.FixtureId
        engineId=$script:W3DiagnosticContext.EngineId
    }
    foreach ($key in $Record.Keys) { $base[$key] = $Record[$key] }
    $json = $base | ConvertTo-Json -Depth 12 -Compress
    [IO.File]::AppendAllText($script:W3DiagnosticContext.Journal,$json+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}

function Write-W3GuardRecord {
    param([Parameter(Mandatory)][string]$Rule,[Parameter(Mandatory)][string]$Operation)
    Write-W3DiagnosticRecord @{
        operation=$Operation; decision='application_guard_refused'; guardRule=$Rule
        nativeStarted=$false; nativeExit=$null; stdout=''; stderr=''
    }
}

function Assert-W3DockerArguments {
    param([Parameter(Mandatory)][string[]]$ArgumentList)
    $hostNames = [Collections.Generic.List[string]]::new()
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        $name = [string]$entry.Key
        if ($name -match '^(?i:PGPASSWORD|POSTGRES_PASSWORD|ConnectionStrings__.+)$' -and
            ![string]::IsNullOrWhiteSpace([string]$entry.Value)) { $hostNames.Add($name) }
    }
    $decision = @{ Allowed=$false; Rule='FILE_ONLY_SECRET_DELIVERY'; ReasonCode=$null
                   HostCredentialEnvironmentNames=@($hostNames.ToArray()) }
    foreach ($argument in $ArgumentList) {
        foreach ($secret in $script:W3Secrets) {
            if ($secret.Length -gt 0 -and $argument.Contains($secret,[StringComparison]::Ordinal)) {
                $decision.ReasonCode='REGISTERED_SECRET_IN_ARGUMENT'; return $decision
            }
        }
        if ($argument -match '(?i)^--?(?:password|passwd|pwd|token|secret|connection[-_]?string)(?:=|$)' -or
            $argument -match '(?i)(?:^|[;?&])(?:password|pwd|access_token|refresh_token|client_secret|api_key)=' -or
            $argument -match '(?i)[a-z][a-z0-9+.-]*://[^/\s:@]+:[^/\s@]+@' -or
            $argument -match '(?i)\bAuthorization\s*:\s*Bearer\s+') {
            $decision.ReasonCode='CREDENTIAL_ARGUMENT_SHAPE_REJECTED'; return $decision
        }
    }
    $isCreation = $ArgumentList.Count -gt 0 -and $ArgumentList[0] -in @('create','run')
    if (!$isCreation) {
        $decision.Allowed=$true; $decision.Rule='NO_SECRET_ARGUMENTS'; return $decision
    }
    if ($hostNames.Count -gt 0) {
        $decision.ReasonCode='HOST_DATABASE_CREDENTIAL_ENVIRONMENT_REJECTED'; return $decision
    }
    for ($i=1; $i -lt $ArgumentList.Count; $i++) {
        $arg = $ArgumentList[$i]
        if ($arg -eq '--env-file' -or $arg.StartsWith('--env-file=')) {
            $decision.ReasonCode='ENV_FILE_REJECTED'; return $decision
        }
        $envValue = $null
        if ($arg -in @('--env','-e')) {
            if (++$i -ge $ArgumentList.Count) {
                $decision.ReasonCode='ENV_VALUE_MISSING'; return $decision
            }
            $envValue = $ArgumentList[$i]
        } elseif ($arg.StartsWith('--env=')) {
            $envValue = $arg.Substring(6)
        } elseif ($arg.StartsWith('-e') -and $arg.Length -gt 2) {
            $envValue = $arg.Substring(2)
        }
        if ($null -ne $envValue) {
            $allowed = $envValue -eq 'POSTGRES_USER=postgres' -or
                $envValue -in @('POSTGRES_PASSWORD_FILE=/run/secrets/admin-password',
                               'POSTGRES_PASSWORD_FILE=/run/secrets/postgres_admin_password') -or
                $envValue -eq 'POSTGRES_INITDB_ARGS=--auth-host=scram-sha-256' -or
                $envValue -match '^ZEKA_M1_GUARD_FIXTURE=zeka-m1-bootstrap-fixture-[0-9a-f]{32}$' -or
                $envValue -eq 'ZEKA_M1_PG17_REGRESSION_CONFIG=/run/setup/provider-regression.json'
            if (!$allowed) {
                $decision.ReasonCode='CONTAINER_ENVIRONMENT_NOT_ALLOWLISTED'; return $decision
            }
        }
    }
    $decision.Allowed=$true
    return $decision
}

function Invoke-W3Native {
    param([Parameter(Mandatory)][string]$Exe,
          [Parameter(Mandatory)][string[]]$ArgumentList,
          [Parameter(Mandatory)][string]$WorkingDirectory,
          [int]$TimeoutMs=120000,
          [Parameter(Mandatory)][string]$Operation,
          [switch]$AllowFailure)
    if ($TimeoutMs -lt 1) { throw [InvalidOperationException]::new('NATIVE_TIMEOUT_INPUT_REJECTED') }
    $guard = @{Allowed=$true;Rule='NO_SECRET_ARGUMENTS';HostCredentialEnvironmentNames=@()}
    foreach ($argument in $ArgumentList) {
        foreach ($secret in $script:W3Secrets) {
            if ($secret.Length -gt 0 -and $argument.Contains($secret,[StringComparison]::Ordinal)) {
                $guard=@{Allowed=$false;Rule='REGISTERED_SECRET_IN_ARGUMENT';HostCredentialEnvironmentNames=@()}
            }
        }
    }
    if ([IO.Path]::GetFileNameWithoutExtension($Exe) -eq 'docker') {
        $guard = Assert-W3DockerArguments -ArgumentList $ArgumentList
    }
    $record = @{
        operation=$Operation; guardRule=$guard.Rule; nativeStarted=$false; nativePid=$null
        nativeExit=$null; stdout=''; stderr=''; hostCredentialEnvironmentNames=$guard.HostCredentialEnvironmentNames
        executionBoundary='inside owner-local launcher; outer approval is recorded by the calling tool'
    }
    if (!$guard.Allowed) {
        $record.decision='application_guard_refused'
        if ($guard.ReasonCode) { $record.guardRule=$guard.ReasonCode }
        Write-W3DiagnosticRecord $record
        throw [InvalidOperationException]::new('W3_ARGUMENT_SECURITY_REJECTED')
    }
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.WorkingDirectory=$WorkingDirectory
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$info
    try {
        try {
            if (!$process.Start()) { throw [InvalidOperationException]::new('NATIVE_START_RETURNED_FALSE') }
        } catch {
            $record.decision='native_process_start_failed'
            $record.stderr=ConvertTo-W3SafeText $_.Exception.Message
            Write-W3DiagnosticRecord $record
            throw [InvalidOperationException]::new('PROCESS_START_FAILED')
        }
        $record.nativeStarted=$true; $record.nativePid=$process.Id; $record.decision='native_started'
        Write-W3DiagnosticRecord $record
        $out=$process.StandardOutput.ReadToEndAsync()
        $err=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutMs)) {
            try { $process.Kill($true) } catch {}
            $closed=$process.WaitForExit(5000)
            $record.decision='native_timeout'
            if ($out.IsCompletedSuccessfully) { $record.stdout=ConvertTo-W3SafeText $out.GetAwaiter().GetResult() }
            if ($err.IsCompletedSuccessfully) { $record.stderr=ConvertTo-W3SafeText $err.GetAwaiter().GetResult() }
            $record.stdoutComplete=$out.IsCompletedSuccessfully; $record.stderrComplete=$err.IsCompletedSuccessfully
            if ($closed) { $record.nativeExit=$process.ExitCode }
            Write-W3DiagnosticRecord $record
            throw [InvalidOperationException]::new('OPERATION_TIMEOUT_REQUIRES_INSPECTION')
        }
        $rawOut=$out.GetAwaiter().GetResult(); $rawError=$err.GetAwaiter().GetResult()
        $exit=$process.ExitCode
        $record.nativeExit=$exit
        $record.stdout=ConvertTo-W3SafeText $rawOut
        $record.stderr=ConvertTo-W3SafeText $rawError
        $record.decision=if($exit -eq 0){'native_completed'}else{'native_nonzero_exit'}
        Write-W3DiagnosticRecord $record
        if ($exit -ne 0 -and !$AllowFailure) {
            throw [InvalidOperationException]::new('NATIVE_OPERATION_REJECTED')
        }
        return @{Exit=$exit;Out=$rawOut;Error=$rawError}
    } finally { $process.Dispose() }
}

function Get-W3MountSource {
    param([Parameter(Mandatory)][string]$WindowsPath)
    if (!('W3.NativePath' -as [type])) {
        Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; namespace W3 { public static class NativePath { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template); [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint length,uint flags); } }'
    }
    $logical=[IO.Path]::GetFullPath($WindowsPath)
    if (!(Test-Path -LiteralPath $logical)) {
        Write-W3GuardRecord -Rule 'MOUNT_SOURCE_MISSING' -Operation 'mount.resolve'
        throw [InvalidOperationException]::new('MOUNT_SOURCE_MISSING')
    }
    $handle=[W3.NativePath]::CreateFileW($logical,0x80,7,[IntPtr]::Zero,3,0x02000000,[IntPtr]::Zero)
    try {
        if ($handle.IsInvalid) { Write-W3GuardRecord -Rule 'MOUNT_SOURCE_HANDLE_REJECTED' -Operation 'mount.resolve'; throw [InvalidOperationException]::new('MOUNT_SOURCE_HANDLE_REJECTED') }
        $buffer=[Text.StringBuilder]::new(32768)
        $length=[W3.NativePath]::GetFinalPathNameByHandle($handle,$buffer,32768,0)
        if (!$length -or $length -ge 32768) {
            Write-W3GuardRecord -Rule 'MOUNT_SOURCE_PHYSICAL_PATH_UNAVAILABLE' -Operation 'mount.resolve'
            throw [InvalidOperationException]::new('MOUNT_SOURCE_PHYSICAL_PATH_UNAVAILABLE')
        }
        $physical=$buffer.ToString()
    } finally { $handle.Dispose() }
    if ($physical.StartsWith('\\?\')) { $physical=$physical.Substring(4) }
    if ($physical -notmatch '^[CD]:\\' -or $physical.Contains(',')) {
        Write-W3GuardRecord -Rule 'MOUNT_SOURCE_LOCAL_DRIVE_REJECTED' -Operation 'mount.resolve'
        throw [InvalidOperationException]::new('MOUNT_SOURCE_LOCAL_DRIVE_REJECTED')
    }
    $wsl=Invoke-W3Native -Exe 'wsl.exe' -ArgumentList @('-d','rancher-desktop','-u','root','--exec','wslpath','-u',$physical.Replace('\','/')) -WorkingDirectory (Get-Location).Path -Operation 'mount.windows_to_wsl'
    $daemon=$wsl.Out.Trim()
    if ($daemon -notmatch '^/mnt/[cd]/[^\r\n,]+$') {
        Write-W3GuardRecord -Rule 'MOUNT_SOURCE_TRANSLATION_REJECTED' -Operation 'mount.resolve'
        throw [InvalidOperationException]::new('MOUNT_SOURCE_TRANSLATION_REJECTED')
    }
    $probe=Invoke-W3Native -Exe 'wsl.exe' -ArgumentList @('-d','rancher-desktop','-u','root','--exec','/bin/busybox','test','-r',$daemon) -WorkingDirectory (Get-Location).Path -Operation 'mount.wsl_readability' -AllowFailure
    if ($probe.Exit -ne 0) {
        Write-W3GuardRecord -Rule 'MOUNT_SOURCE_WSL_UNREADABLE' -Operation 'mount.resolve'
        throw [InvalidOperationException]::new('MOUNT_SOURCE_WSL_UNREADABLE')
    }
    $result=@{WindowsLogical=$logical;WindowsPhysical=$physical;DaemonPath=$daemon;Readable=$true;SourceExists=$true}
    Write-W3DiagnosticRecord @{operation='mount.resolve';decision='metadata_verified';nativeStarted=$false;nativeExit=$null;mount=$result}
    return $result
}

function Complete-W3AtomicFileWrite {
    param([Parameter(Mandatory)][string]$TemporaryPath,
          [Parameter(Mandatory)][string]$DestinationPath,
          [switch]$Replace)
    $source=[IO.Path]::GetFullPath($TemporaryPath)
    $destination=[IO.Path]::GetFullPath($DestinationPath)
    if (![StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetDirectoryName($source),[IO.Path]::GetDirectoryName($destination))) {
        throw [InvalidOperationException]::new('ATOMIC_WRITE_CROSS_DIRECTORY_REJECTED')
    }
    if (Test-Path -LiteralPath $destination) {
        if (!$Replace) { throw [InvalidOperationException]::new('ATOMIC_WRITE_OVERWRITE_REJECTED') }
        # A PowerShell $null string argument binds as empty; NullString passes a real null backup path.
        [IO.File]::Replace($source,$destination,[NullString]::Value)
    } else { [IO.File]::Move($source,$destination) }
}
