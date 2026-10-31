# Test-only isolation. Keep this name policy identical to Assert-W3DockerArguments; never inspect values.
function Remove-CoreCoverageDatabaseEnvironmentNames {
    param([Parameter(Mandatory)][Collections.Generic.IDictionary[string,string]]$Environment)
    $names = @($Environment.Keys | Where-Object { $_ -match '^(?i:PGPASSWORD|POSTGRES_PASSWORD|ConnectionStrings__.+)$' } | Sort-Object)
    foreach ($name in $names) { $null = $Environment.Remove($name) }
    return $names
}
function New-CoreCoverageChildStartInfo {
    param([Parameter(Mandatory)][string]$Directory)
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',
        (Join-Path $PSScriptRoot 'Test-PowerShellLineCoverage.ps1'),'-Directory',$Directory)) {
        $start.ArgumentList.Add($argument)
    }
    $removed = @(Remove-CoreCoverageDatabaseEnvironmentNames $start.Environment)
    return [pscustomobject]@{ StartInfo=$start; RemovedEnvironmentNames=$removed }
}
function Save-CoreCoverageChildReceipt([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 6
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes=[Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
}
function Invoke-CoreCoverageChild {
    param([Parameter(Mandatory)][string]$Directory)
    $core = Join-Path $Directory 'core'
    $launch = New-CoreCoverageChildStartInfo -Directory $core
    $receipt = [ordered]@{
        status='FAIL'; stage='child-core-coverage'; childExitCode=$null; code='CORE_COVERAGE_CHILD_START_FAILED'
        removedEnvironmentNames=$launch.RemovedEnvironmentNames
        parentEnvironmentModified=$false; stdoutLogged=$false; stderrLogged=$false
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $launch.StartInfo
    try {
        if (!$process.Start()) { throw 'CORE_COVERAGE_CHILD_START_FAILED' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(120000)) {
            $process.Kill($true); $null=$process.WaitForExit(5000)
            $receipt.code='CORE_COVERAGE_CHILD_TIMEOUT'
        } elseif (!([Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout,$stderr))).Wait(5000)) {
            $receipt.code='CORE_COVERAGE_CHILD_OUTPUT_DRAIN_FAILED'
        } else {
            $receipt.childExitCode=$process.ExitCode
            $receipt.code=if($process.ExitCode -eq 0){'CORE_COVERAGE_CHILD_PASSED'}else{'CORE_COVERAGE_CHILD_TEST_FAILED'}
            if ($process.ExitCode -eq 0) { $receipt.status='PASS' }
        }
    } catch { $receipt.code='CORE_COVERAGE_CHILD_PROCESS_FAILED' }
    finally { $process.Dispose() }
    foreach ($name in @('powershell-coverage.xml','powershell-coverage-result.json')) {
        $path = Join-Path $core $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Copy($path, (Join-Path $Directory $name), $false) }
    }
    $native = [ordered]@{ schema='zeka-core-native-receipt/v1'; present=$false; valid=$false }
    $nativePath = Join-Path $core 'native-diagnostics/diagnostic-tests-result.json'
    if (Test-Path -LiteralPath $nativePath -PathType Leaf) {
        $native.present=$true
        try {
            $inputReceipt = [IO.File]::ReadAllText($nativePath) | ConvertFrom-Json
            $cases = @('test-admission','registered-text-redacted-with-container-id-preserved',
                'file-only-docker-arguments-admitted-without-execution','credential-and-forwarded-environment-argument-shapes-refused',
                'docker-guard-refusal-recorded-before-process-start','nonzero-native-output-captured-with-allow-failure',
                'nonzero-native-output-persisted-before-throw','native-start-failure-recorded-without-a-process',
                'explicit-application-guard-record-identifies-rule','all-records-correlated-and-no-docker-process-started')
            if ($inputReceipt.schema -ceq 'zeka-m1-native-diagnostic-tests/v1' -and $inputReceipt.status -cin @('PASS','FAIL') -and
                ($inputReceipt.passed -is [long] -or $inputReceipt.passed -is [int]) -and
                ($inputReceipt.failed -is [long] -or $inputReceipt.failed -is [int]) -and
                $inputReceipt.passed -ge 0 -and $inputReceipt.passed -le 9 -and $inputReceipt.failed -in @(0,1)) {
                $native.valid=$true; $native.status=$inputReceipt.status
                $native.passed=[int]$inputReceipt.passed; $native.failed=[int]$inputReceipt.failed
                $native.failedCase=if($inputReceipt.failedCase -in $cases){$inputReceipt.failedCase}else{$null}
                $native.failureCode=if($inputReceipt.failureCode -cmatch '^TEST_[A-Z0-9_]{1,100}$'){$inputReceipt.failureCode}else{$null}
            }
        } catch { $native.valid=$false }
    }
    if ($receipt.status -eq 'PASS' -and (!$native.valid -or $native.status -ne 'PASS' -or $native.passed -ne 9 -or $native.failed -ne 0 -or
        !(Test-Path -LiteralPath (Join-Path $Directory 'powershell-coverage.xml')) -or
        !(Test-Path -LiteralPath (Join-Path $Directory 'powershell-coverage-result.json')))) {
        $receipt.status='FAIL'; $receipt.code='CORE_COVERAGE_CHILD_RECEIPTS_REJECTED'
    }
    Save-CoreCoverageChildReceipt (Join-Path $Directory 'native-result.json') $native
    Save-CoreCoverageChildReceipt (Join-Path $Directory 'launcher-result.json') $receipt
    return [pscustomobject]$receipt
}
