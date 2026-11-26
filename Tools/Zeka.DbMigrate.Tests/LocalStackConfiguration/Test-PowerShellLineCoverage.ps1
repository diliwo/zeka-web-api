[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'COVERAGE_OWNER_WINDOWS_REQUIRED' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output = [IO.Path]::GetFullPath($Directory)
if ($output -eq $root -or $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'COVERAGE_OUTPUT_INSIDE_CHECKOUT_REJECTED'
}
if (Test-Path -LiteralPath $output) { throw 'COVERAGE_NEW_OUTPUT_DIRECTORY_REQUIRED' }
[IO.Directory]::CreateDirectory($output) | Out-Null
. (Join-Path $PSScriptRoot 'PowerShellLineCoverage.ps1')
$fixture = Join-Path $output 'collector-proof.ps1'
$fixtureText = [string]::Join("`n", @(
    'param([switch]$TakeBranch)',
    '$answer = 1',
    'if ($TakeBranch) {',
    '    $answer = 2',
    '}',
    'function Invoke-Unused {',
    '    $unused = 99',
    '}',
    '$answer += 1',
    '$shortCircuit = $false -and',
    '    [bool](& { throw ''COVERAGE_UNREACHABLE_BRANCH'' })',
    '$answer += 1'
))
[IO.File]::WriteAllText($fixture, $fixtureText + "`n", [Text.UTF8Encoding]::new($false))
$proofReport = Join-Path $output 'collector-proof.xml'
$null = Invoke-PowerShellLineCoverage -SourceRoot $output -SourceFile @('collector-proof.ps1') -ReportFile $proofReport -TestAction {
    & $fixture
}
[xml]$proof = [IO.File]::ReadAllText($proofReport)
foreach ($line in @(2, 9, 10, 12)) {
    if (@($proof.coverage.file.lineToCover | Where-Object { $_.lineNumber -eq $line -and $_.covered -eq 'true' }).Count -ne 1) {
        throw 'COVERAGE_EXECUTED_LINE_NOT_RECORDED'
    }
}
foreach ($line in @(4, 7, 11)) {
    if (@($proof.coverage.file.lineToCover | Where-Object { $_.lineNumber -eq $line -and $_.covered -eq 'false' }).Count -ne 1) {
        throw 'COVERAGE_UNEXECUTED_LINE_REPORTED_AS_HIT'
    }
}
$report = Join-Path $output 'powershell-coverage.xml'
$nativeDirectory = Join-Path $output 'native-diagnostics'
$atomicDirectory = Join-Path $output 'atomic-write'
$regressionCounts=[ordered]@{native=0;atomic=0;ownerAdmission=0;workerRotation=0;authHttps=0;safePostgres=0;preflightPrivileges=0;queryMetadata=0}
$script:CoverageStage='native-diagnostics'
function Assert-CoreSynthetic([bool]$Value){if(!$Value){throw 'COVERAGE_SYNTHETIC_CASES_REJECTED'}}
function Read-CoreSyntheticJson([string]$Path){return ([IO.File]::ReadAllText($Path)|ConvertFrom-Json)}
try {
$sourceHashes = Invoke-PowerShellLineCoverage -SourceRoot $root -SourceFile @(
    'Deployments/local-stack/W3-NativeDiagnostics.ps1',
    'Deployments/local-stack/Initialize-Databases.ps1',
    'Deployments/local-stack/Invoke-OwnerFirstAdmin.ps1',
    'Deployments/local-stack/Invoke-WorkerCredentialRotation.ps1',
    'Deployments/local-stack/Initialize-AuthHttpsConfiguration.ps1',
    'Deployments/local-stack/SafePostgresDiagnostics.ps1',
    'Deployments/local-stack/OwnerFirstAdminPreflightQueries.ps1',
    'Deployments/local-stack/OwnerFirstAdminPreflightPrivileges.ps1',
    'Deployments/local-stack/OwnerFirstAdminRlsMetadata.ps1'
) -ReportFile $report -TestAction {
    & (Join-Path $PSScriptRoot 'Test-W3NativeDiagnostics.ps1') -Directory $nativeDirectory | Out-Null
    $native = [IO.File]::ReadAllText((Join-Path $nativeDirectory 'diagnostic-tests-result.json')) | ConvertFrom-Json
    if ($native.status -ne 'PASS' -or $native.passed -ne 9 -or $native.failed -ne 0) { throw 'COVERAGE_NATIVE_NINE_CASES_FAILED' }
    Assert-CoreSynthetic ($native.dockerProcessesStarted-eq0-and$native.nativeProcessesStarted-eq2)
    $regressionCounts.native=9
    $script:CoverageStage='atomic-write'
    & (Join-Path $PSScriptRoot 'Test-W3AtomicWrite.ps1') -Directory $atomicDirectory | Out-Null
    $atomic = [IO.File]::ReadAllText((Join-Path $atomicDirectory 'atomic-write-result.json')) | ConvertFrom-Json
    if ($atomic.status -ne 'PASS' -or $atomic.passed -ne 2 -or $atomic.failed -ne 0) { throw 'COVERAGE_ATOMIC_TWO_CASES_FAILED' }
    $regressionCounts.atomic=2
  $script:CoverageStage='owner-admission'
  $ownerReceipt=Join-Path $output 'owner-admission.json'
  & (Join-Path $root 'Tools/Zeka.AuthOwnerProvision.Tests/OperatorBoundary/Test-OwnerAdmission.ps1') -ReportFile $ownerReceipt|Out-Null
  $owner=Read-CoreSyntheticJson $ownerReceipt
  Assert-CoreSynthetic ($owner.status-ceq'PASS'-and$owner.count-eq168-and!$owner.runtimeOperationsExecuted)
    $regressionCounts.ownerAdmission=[int]$owner.count

  $script:CoverageStage='worker-rotation'
  $workerOutput=& (Join-Path $root 'Tools/Zeka.AuthOwnerProvision.Tests/WorkerRotation/Test-WorkerRotation.ps1')|Out-String
  $worker=$workerOutput|ConvertFrom-Json
  Assert-CoreSynthetic ($worker.tests-gt0-and$worker.passed-eq$worker.tests-and$worker.failed-eq0-and$worker.runtime-ceq'NOT_RUN'-and$worker.provider-ceq'NOT_RUN'-and$worker.adoption-ceq'NOT_RUN')
    $regressionCounts.workerRotation=[int]$worker.passed

  $script:CoverageStage='auth-https-configuration'
  $authDirectory=Join-Path $output 'auth-https-configuration'
  & (Join-Path $root 'Tools/Zeka.DbMigrate.Tests/LocalStackConfiguration/Test-AuthHttpsConfiguration.ps1') -Directory $authDirectory|Out-Null
  $auth=Read-CoreSyntheticJson (Join-Path $authDirectory 'auth-https-tests-result.json')
  Assert-CoreSynthetic ($auth.status-ceq'PASS'-and$auth.assertionsPassed-gt0-and$auth.syntheticInputsOnly-and!$auth.validCertificatesCreated-and!$auth.privateKeysCreated-and!$auth.dockerExecuted-and!$auth.trustInstalled-and!$auth.runtimeReadinessEstablished)
    $regressionCounts.authHttps=[int]$auth.assertionsPassed

  $script:CoverageStage='safe-postgres-diagnostics'
  $safeReceipt=Join-Path $output 'safe-postgres-diagnostics.json'
  & (Join-Path $root 'Tools/Zeka.AuthOwnerProvision.Tests/OperatorBoundary/Test-SafePostgresDiagnostics.ps1') -ReportFile $safeReceipt|Out-Null
  $safe=Read-CoreSyntheticJson $safeReceipt
  Assert-CoreSynthetic ($safe.status-ceq'PASS'-and$safe.count-gt0-and!$safe.realDatabaseAccess-and!$safe.runtimeOperationsExecuted)
    $regressionCounts.safePostgres=[int]$safe.count

  $script:CoverageStage='owner-preflight-privileges'
  $privilegeReceipt=Join-Path $output 'owner-preflight-privileges.json'
  & (Join-Path $root 'Tools/Zeka.AuthOwnerProvision.Tests/OperatorBoundary/Test-OwnerPreflightPrivileges.ps1') -ReportFile $privilegeReceipt|Out-Null
  $privilege=Read-CoreSyntheticJson $privilegeReceipt
  Assert-CoreSynthetic ($privilege.status-ceq'PASS'-and$privilege.count-gt0-and!$privilege.sqlExecuted-and!$privilege.runtimeOperationsExecuted)
    $regressionCounts.preflightPrivileges=[int]$privilege.count
  $script:CoverageStage='owner-preflight-queries'
  $queryReceipt=Join-Path $output 'owner-preflight-queries.json'
  & (Join-Path $root 'Tools/Zeka.AuthOwnerProvision.Tests/OperatorBoundary/Test-OwnerPreflightQueries.ps1') -ReportFile $queryReceipt|Out-Null
  $query=Read-CoreSyntheticJson $queryReceipt
  Assert-CoreSynthetic ($query.status-ceq'PASS'-and$query.count-eq201-and!$query.sqlExecuted-and!$query.runtimeOperationsExecuted-and$query.behavioralRlsProof-ceq'NOT_RUN')
    $regressionCounts.queryMetadata=[int]$query.count
}
} catch {
    # Preserve the first safe category/stage before the child adapter handles the native exit.
    $failure=[ordered]@{status='FAIL';collectorProof='NOT_VERIFIED';stage=$script:CoverageStage;
        firstExceptionType=$_.Exception.GetType().FullName;code='COVERAGE_SYNTHETIC_REGRESSION_FAILED';
        regressionCounts=$regressionCounts;rawExceptionPublished=$false}
    $failure|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'powershell-coverage-result.json') -Encoding utf8
    throw
}
[xml]$coverage = [IO.File]::ReadAllText($report)
$nativeFile = $coverage.coverage.file | Where-Object { $_.path -eq 'Deployments/local-stack/W3-NativeDiagnostics.ps1' }
$initFile = $coverage.coverage.file | Where-Object { $_.path -eq 'Deployments/local-stack/Initialize-Databases.ps1' }
$nativeHits = @($nativeFile.lineToCover | Where-Object covered -eq 'true').Count
$initHits = @($initFile.lineToCover | Where-Object covered -eq 'true').Count
if ($coverage.coverage.version -ne '1' -or @($coverage.coverage.file).Count -ne 9 -or $nativeHits -le 0 -or $initHits -ne 0) {
    throw 'COVERAGE_REAL_SOURCE_COUNTS_REJECTED'
}
$sourceCounts=@($coverage.coverage.file|ForEach-Object{
    [ordered]@{path=$_.path;statementLines=@($_.lineToCover).Count;executedLines=@($_.lineToCover|Where-Object covered -CEQ 'true').Count}
})
$expectedCounts=[ordered]@{native=9;atomic=2;ownerAdmission=168;workerRotation=129;authHttps=11;safePostgres=60;preflightPrivileges=97;queryMetadata=201}
foreach($name in $expectedCounts.Keys){if($regressionCounts[$name]-ne$expectedCounts[$name]){throw 'COVERAGE_SYNTHETIC_COUNTS_REJECTED'}}
$summary = [ordered]@{
    status='PASS'; collectorProof='PASS'; nativeCasesPassed=9; atomicCasesPassed=2
    regressionCounts=$regressionCounts; sourceCounts=$sourceCounts
    scope='Synthetic admission/configuration assertions only; no SQL, Docker or service execution'
    workerRuntime='NOT_RUN'; workerProvider='NOT_RUN'; workerAdoption='NOT_RUN'; authOwnerChange='NOT_RUN'; behavioralRlsProof='NOT_RUN'
    validAuthCertificatesCreated=$false; privateAuthKeysCreated=$false; trustInstalled=$false
    nativeStatementLines=@($nativeFile.lineToCover).Count; nativeExecutedLines=$nativeHits
    initializationStatementLines=@($initFile.lineToCover).Count; initializationExecutedLines=$initHits
    initialization='NOT RUN; report records zero hits, not database bootstrap proof'
    collector='PowerShell line breakpoints; actual matching call-stack ScriptName/ScriptLineNumber only; no context or values serialized'
    sourceHashes=$sourceHashes; coverageReport='powershell-coverage.xml'
    coverageSha256=(Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'powershell-coverage-result.json') -Encoding utf8
Write-Output ($summary | ConvertTo-Json -Depth 6 -Compress)
