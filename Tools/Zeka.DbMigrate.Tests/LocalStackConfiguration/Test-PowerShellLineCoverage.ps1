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
$sourceHashes = Invoke-PowerShellLineCoverage -SourceRoot $root -SourceFile @(
    'Deployments/local-stack/W3-NativeDiagnostics.ps1',
    'Deployments/local-stack/Initialize-Databases.ps1'
) -ReportFile $report -TestAction {
    & (Join-Path $PSScriptRoot 'Test-W3NativeDiagnostics.ps1') -Directory $nativeDirectory | Out-Null
    $native = [IO.File]::ReadAllText((Join-Path $nativeDirectory 'diagnostic-tests-result.json')) | ConvertFrom-Json
    if ($native.status -ne 'PASS' -or $native.passed -ne 9 -or $native.failed -ne 0) { throw 'COVERAGE_NATIVE_NINE_CASES_FAILED' }
    & (Join-Path $PSScriptRoot 'Test-W3AtomicWrite.ps1') -Directory $atomicDirectory | Out-Null
    $atomic = [IO.File]::ReadAllText((Join-Path $atomicDirectory 'atomic-write-result.json')) | ConvertFrom-Json
    if ($atomic.status -ne 'PASS' -or $atomic.passed -ne 2 -or $atomic.failed -ne 0) { throw 'COVERAGE_ATOMIC_TWO_CASES_FAILED' }
}
[xml]$coverage = [IO.File]::ReadAllText($report)
$nativeFile = $coverage.coverage.file | Where-Object { $_.path -eq 'Deployments/local-stack/W3-NativeDiagnostics.ps1' }
$initFile = $coverage.coverage.file | Where-Object { $_.path -eq 'Deployments/local-stack/Initialize-Databases.ps1' }
$nativeHits = @($nativeFile.lineToCover | Where-Object covered -eq 'true').Count
$initHits = @($initFile.lineToCover | Where-Object covered -eq 'true').Count
if ($coverage.coverage.version -ne '1' -or @($coverage.coverage.file).Count -ne 2 -or $nativeHits -le 0 -or $initHits -ne 0) {
    throw 'COVERAGE_REAL_SOURCE_COUNTS_REJECTED'
}
$summary = [ordered]@{
    status='PASS'; collectorProof='PASS'; nativeCasesPassed=9; atomicCasesPassed=2
    nativeStatementLines=@($nativeFile.lineToCover).Count; nativeExecutedLines=$nativeHits
    initializationStatementLines=@($initFile.lineToCover).Count; initializationExecutedLines=$initHits
    initialization='NOT RUN; report records zero hits, not database bootstrap proof'
    collector='PowerShell line breakpoints; actual matching call-stack ScriptName/ScriptLineNumber only; no context or values serialized'
    sourceHashes=$sourceHashes; coverageReport='powershell-coverage.xml'
    coverageSha256=(Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'powershell-coverage-result.json') -Encoding utf8
Write-Output ($summary | ConvertTo-Json -Depth 6 -Compress)
