[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory
)
$ErrorActionPreference = 'Stop'
$caseName = 'test-admission'
$testResults = [Collections.Generic.List[object]]::new()
$journal = $null
$knownSecret = $null
$fullDirectory = [IO.Path]::GetFullPath($Directory)
$workingDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
function Assert-Check([bool]$Condition, [string]$Code) {
    if (!$Condition) { throw [InvalidOperationException]::new($Code) }
}
function Read-TestJournal {
    return @(Get-Content -LiteralPath $journal | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
function Add-Pass {
    $testResults.Add([ordered]@{ name = $caseName; status = 'PASS' })
}
function Assert-NativeCompletion($Record, [string]$Operation) {
    Assert-Check ($Record.operation -eq $Operation) 'TEST_OPERATION_NOT_PRESERVED'
    Assert-Check ($Record.nativeStarted -eq $true -and $Record.nativeExit -eq 7) 'TEST_NATIVE_EXIT_NOT_PRESERVED'
    Assert-Check ($Record.decision -eq 'native_nonzero_exit') 'TEST_NATIVE_DECISION_NOT_PRESERVED'
    Assert-Check ($Record.stdout.Trim() -eq 'W3_NATIVE_TEST_STDOUT') 'TEST_STDOUT_NOT_PRESERVED'
    Assert-Check ($Record.stderr.Trim() -eq 'W3_NATIVE_TEST_STDERR') 'TEST_STDERR_NOT_PRESERVED'
}
try {
    Assert-Check $IsWindows 'TEST_OWNER_WINDOWS_REQUIRED'
    Assert-Check (!(Test-Path -LiteralPath $fullDirectory)) 'TEST_NEW_EVIDENCE_DIRECTORY_REQUIRED'
    . (Join-Path $PSScriptRoot '../../../Deployments/local-stack/W3-NativeDiagnostics.ps1')
    $fixtureId = [Guid]::NewGuid().ToString('N')
    $engineId = 'diagnostics-test-engine-' + [Guid]::NewGuid().ToString('N')
    $journal = Initialize-W3Diagnostics -Directory $fullDirectory -FixtureId $fixtureId -EngineId $engineId

    $caseName = 'registered-text-redacted-with-container-id-preserved'
    # Synthetic in-memory data only: it is never passed to a native process or environment.
    $knownSecret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $containerIdentifier = 'b' * 64
    Register-W3Secret -Value $knownSecret
    $safeText = ConvertTo-W3SafeText -Text ('provider error data=' + $knownSecret + '; dockerObject=' + $containerIdentifier)
    Assert-Check (!$safeText.Contains($knownSecret, [StringComparison]::Ordinal)) 'TEST_REGISTERED_TEXT_NOT_REDACTED'
    Assert-Check ($safeText.Contains($containerIdentifier, [StringComparison]::Ordinal)) 'TEST_CONTAINER_IDENTIFIER_OVERREDACTED'
    Assert-Check ($safeText.Contains('provider error data=', [StringComparison]::Ordinal)) 'TEST_ENTIRE_NATIVE_ERROR_SUPPRESSED'
    Add-Pass

    $caseName = 'file-only-docker-arguments-admitted-without-execution'
    $fileOnly = @(
        'create',
        '--env', 'POSTGRES_USER=postgres',
        '--env', 'POSTGRES_PASSWORD_FILE=/run/secrets/admin-password',
        '--mount', ('type=bind,src=' + $PSCommandPath + ',dst=/run/secrets/admin-password,readonly'),
        'postgres:17'
    )
    $fileDecision = Assert-W3DockerArguments -ArgumentList $fileOnly
    Assert-Check ($fileDecision.Allowed -eq $true) 'TEST_FILE_ONLY_ARGUMENTS_REJECTED'
    Assert-Check ($fileDecision.Rule -eq 'FILE_ONLY_SECRET_DELIVERY') 'TEST_FILE_DELIVERY_RULE_NOT_IDENTIFIED'
    Add-Pass

    $caseName = 'credential-and-forwarded-environment-argument-shapes-refused'
    $badArgumentCases = @(
        @{ Arguments = @('create', '--password', 'postgres:17'); Rule = 'CREDENTIAL_ARGUMENT_SHAPE_REJECTED' },
        @{ Arguments = @('create', '--env', 'POSTGRES_PASSWORD', 'postgres:17'); Rule = 'CONTAINER_ENVIRONMENT_NOT_ALLOWLISTED' },
        @{ Arguments = @('create', '--env-file', 'unused-diagnostic-input.env', 'postgres:17'); Rule = 'ENV_FILE_REJECTED' },
        @{ Arguments = @('create', '--env', 'ZEKA_W3_FORWARDED_ENVIRONMENT', 'postgres:17'); Rule = 'CONTAINER_ENVIRONMENT_NOT_ALLOWLISTED' },
        @{ Arguments = @('create', '--env', 'ConnectionStrings__Default', 'postgres:17'); Rule = 'CONTAINER_ENVIRONMENT_NOT_ALLOWLISTED' }
    )
    foreach ($badCase in $badArgumentCases) {
        $decision = Assert-W3DockerArguments -ArgumentList $badCase.Arguments
        Assert-Check (!$decision.Allowed -and $decision.ReasonCode -eq $badCase.Rule) 'TEST_CREDENTIAL_ARGUMENT_NOT_PRECISELY_REFUSED'
    }
    Add-Pass

    $caseName = 'docker-guard-refusal-recorded-before-process-start'
    $refused = $false
    # This invalid, non-mutating command is a safety backstop if the guard regresses.
    # The expected path refuses it before Docker starts; no credential value is supplied.
    try {
        $null = Invoke-W3Native -Exe 'docker' -ArgumentList @('version', '--password') -WorkingDirectory $workingDirectory -TimeoutMs 10000 -Operation 'test.docker_guard'
    } catch { $refused = $_.Exception.Message -eq 'W3_ARGUMENT_SECURITY_REJECTED' }
    Assert-Check $refused 'TEST_DOCKER_GUARD_DID_NOT_REFUSE'
    $dockerRecords = @(Read-TestJournal | Where-Object operation -EQ 'test.docker_guard')
    Assert-Check ($dockerRecords.Count -eq 1) 'TEST_DOCKER_GUARD_RECORD_COUNT'
    Assert-Check ($dockerRecords[0].nativeStarted -eq $false -and $null -eq $dockerRecords[0].nativeExit) 'TEST_DOCKER_PROCESS_STARTED'
    Assert-Check ($dockerRecords[0].decision -eq 'application_guard_refused' -and $dockerRecords[0].guardRule -eq 'CREDENTIAL_ARGUMENT_SHAPE_REJECTED') 'TEST_DOCKER_GUARD_RULE_NOT_PRESERVED'
    Add-Pass

    $childScript = '[Console]::Out.WriteLine("W3_NATIVE_TEST_STDOUT"); [Console]::Error.WriteLine("W3_NATIVE_TEST_STDERR"); exit 7'
    $childArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', $childScript)
    $pwsh = Join-Path $PSHOME 'pwsh.exe'

    $caseName = 'nonzero-native-output-captured-with-allow-failure'
    $native = Invoke-W3Native -Exe $pwsh -ArgumentList $childArguments -WorkingDirectory $workingDirectory -TimeoutMs 15000 -Operation 'test.native_allow_failure' -AllowFailure
    Assert-Check ($native.Exit -eq 7) 'TEST_RETURNED_EXIT_NOT_PRESERVED'
    Assert-Check ($native.Out.Trim() -eq 'W3_NATIVE_TEST_STDOUT' -and $native.Error.Trim() -eq 'W3_NATIVE_TEST_STDERR') 'TEST_RETURNED_OUTPUT_NOT_PRESERVED'
    $nativeRecords = @(Read-TestJournal | Where-Object operation -EQ 'test.native_allow_failure')
    Assert-Check ($nativeRecords.Count -eq 2 -and $nativeRecords[0].decision -eq 'native_started') 'TEST_NATIVE_START_RECORD_MISSING'
    Assert-NativeCompletion $nativeRecords[-1] 'test.native_allow_failure'
    Add-Pass

    $caseName = 'nonzero-native-output-persisted-before-throw'
    $rejected = $false
    $recordVisibleAtThrow = $false
    try {
        $null = Invoke-W3Native -Exe $pwsh -ArgumentList $childArguments -WorkingDirectory $workingDirectory -TimeoutMs 15000 -Operation 'test.native_reject_failure'
    } catch {
        $rejected = $_.Exception.Message -eq 'NATIVE_OPERATION_REJECTED'
        $recordsAtThrow = @(Read-TestJournal | Where-Object operation -EQ 'test.native_reject_failure')
        $recordVisibleAtThrow = $recordsAtThrow.Count -eq 2 -and $recordsAtThrow[-1].nativeExit -eq 7
    }
    Assert-Check ($rejected -and $recordVisibleAtThrow) 'TEST_ERROR_RECORD_NOT_DURABLE_BEFORE_THROW'
    $failedRecords = @(Read-TestJournal | Where-Object operation -EQ 'test.native_reject_failure')
    Assert-NativeCompletion $failedRecords[-1] 'test.native_reject_failure'
    Add-Pass

    $caseName = 'native-start-failure-recorded-without-a-process'
    $missingExe = Join-Path $fullDirectory ('nonexistent-native-' + [Guid]::NewGuid().ToString('N') + '.exe')
    Assert-Check (!(Test-Path -LiteralPath $missingExe)) 'TEST_NONEXISTENT_EXECUTABLE_PRECONDITION'
    $startRejected = $false
    try {
        $null = Invoke-W3Native -Exe $missingExe -ArgumentList @('--diagnostic-test') -WorkingDirectory $workingDirectory -TimeoutMs 10000 -Operation 'test.native_start_failure' -AllowFailure
    } catch { $startRejected = $_.Exception.Message -eq 'PROCESS_START_FAILED' }
    Assert-Check $startRejected 'TEST_START_FAILURE_NOT_REPORTED'
    $startRecords = @(Read-TestJournal | Where-Object operation -EQ 'test.native_start_failure')
    Assert-Check ($startRecords.Count -eq 1 -and $startRecords[0].nativeStarted -eq $false -and $null -eq $startRecords[0].nativeExit) 'TEST_START_FAILURE_PROCESS_STATE'
    Assert-Check ($startRecords[0].decision -eq 'native_process_start_failed' -and ![string]::IsNullOrWhiteSpace($startRecords[0].stderr)) 'TEST_START_FAILURE_ERROR_SUPPRESSED'
    Add-Pass

    $caseName = 'explicit-application-guard-record-identifies-rule'
    Write-W3GuardRecord -Rule 'TEST_EXPLICIT_GUARD' -Operation 'test.explicit_guard'
    $guardRecords = @(Read-TestJournal | Where-Object operation -EQ 'test.explicit_guard')
    Assert-Check ($guardRecords.Count -eq 1 -and $guardRecords[0].nativeStarted -eq $false -and $guardRecords[0].guardRule -eq 'TEST_EXPLICIT_GUARD') 'TEST_EXPLICIT_GUARD_RECORD_REJECTED'
    Add-Pass

    $caseName = 'all-records-correlated-and-no-docker-process-started'
    $allRecords = @(Read-TestJournal)
    Assert-Check ($allRecords.Count -eq 7) 'TEST_JOURNAL_RECORD_COUNT'
    foreach ($record in $allRecords) {
        Assert-Check ($record.fixtureId -eq $fixtureId -and $record.engineId -eq $engineId -and $record.schema -eq 'zeka-m1-native-diagnostic/v1') 'TEST_DIAGNOSTIC_CORRELATION_LOST'
    }
    $nativeStarts = @($allRecords | Where-Object decision -EQ 'native_started')
    Assert-Check ($nativeStarts.Count -eq 2) 'TEST_UNEXPECTED_NATIVE_PROCESS_COUNT'
    Assert-Check (@($allRecords | Where-Object { $_.operation -eq 'test.docker_guard' -and $_.nativeStarted }).Count -eq 0) 'TEST_DOCKER_NATIVE_PROCESS_STARTED'
    Add-Pass

    $summary = [ordered]@{
        schema = 'zeka-m1-native-diagnostic-tests/v1'; status = 'PASS'
        passed = $testResults.Count; failed = 0; cases = @($testResults.ToArray())
        nativeProcessesStarted = 2; dockerProcessesStarted = 0
        fixtureId = $fixtureId; engineId = $engineId; journal = $journal
        scope = 'Synthetic text and two constant-output PowerShell children; no Docker resources, WSL, private credentials or mount probe.'
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fullDirectory 'diagnostic-tests-result.json')
    Write-Output ($summary | ConvertTo-Json -Depth 10 -Compress)
} catch {
    $failureCode = 'TEST_OPERATION_REJECTED'
    if ($_.Exception -is [InvalidOperationException] -and $_.Exception.Message -match '^TEST_[A-Z0-9_]+$') { $failureCode = $_.Exception.Message }
    $summary = [ordered]@{
        schema = 'zeka-m1-native-diagnostic-tests/v1'; status = 'FAIL'
        passed = $testResults.Count; failed = 1; failedCase = $caseName; failureCode = $failureCode
        cases = @($testResults.ToArray()); journal = $journal; evidencePreserved = $true
    }
    if (Test-Path -LiteralPath $fullDirectory) {
        $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fullDirectory 'diagnostic-tests-result.json')
    }
    Write-Output ($summary | ConvertTo-Json -Depth 10 -Compress)
    exit 1
} finally { $knownSecret = $null }