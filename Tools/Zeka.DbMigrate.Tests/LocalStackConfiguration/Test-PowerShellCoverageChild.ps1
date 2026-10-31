[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
if (!$IsWindows) { throw 'CORE_COVERAGE_CHILD_WINDOWS_REQUIRED' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$output=[IO.Path]::GetFullPath($Directory)
if ($output -eq $root -or $output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'CORE_COVERAGE_CHILD_NEW_OUTSIDE_DIRECTORY_REQUIRED' }
[IO.Directory]::CreateDirectory($output)|Out-Null
. (Join-Path $PSScriptRoot 'PowerShellCoverageChild.ps1')
$checks=[Collections.Generic.List[string]]::new()
function Require-CoreCoverageChild([bool]$Condition,[string]$Name) {
    $proof.stage=$Name
    if (!$Condition) { throw 'CORE_COVERAGE_CHILD_PROOF_FAILED' }
    $checks.Add($Name)
}
$proof=[ordered]@{status='FAIL';code='CORE_COVERAGE_CHILD_PROOF_FAILED';stage='setup';assertionsPassed=0;assertions=@();syntheticValuesLogged=$false}
$fakeName='ConnectionStrings__ZekaCoverageProof_'+[Guid]::NewGuid().ToString('N')
$keepName='ZEKA_COVERAGE_KEEP_'+[Guid]::NewGuid().ToString('N')
$fakeValue='NON_SENSITIVE_COVERAGE_PARENT_MARKER'
$keepValue='NON_SENSITIVE_COVERAGE_KEEP_MARKER'
if (@([Environment]::GetEnvironmentVariables('Process').Keys|Where-Object {$_ -in @($fakeName,$keepName)}).Count -ne 0) {
    throw 'CORE_COVERAGE_CHILD_FIXTURE_NAME_EXISTS'
}
try {
    $dictionary=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $removedExpected=@('PGPASSWORD','pgpassword','POSTGRES_PASSWORD','ConnectionStrings__Synthetic')
    $keptExpected=@('ConnectionStrings__','XPGPASSWORD','POSTGRES_PASSWORD_FILE','ConnectionStrings_Almost','UNRELATED')
    foreach($name in @($removedExpected;$keptExpected)){$dictionary.Add($name,'NON_SENSITIVE_DICTIONARY_MARKER')}
    $removed=@(Remove-CoreCoverageDatabaseEnvironmentNames $dictionary)
    Require-CoreCoverageChild ((($removed|Sort-Object)-join '|') -ceq (($removedExpected|Sort-Object)-join '|')) 'only_exact_guard_matching_names_removed'
    Require-CoreCoverageChild (@($removedExpected|Where-Object {$dictionary.ContainsKey($_)}).Count -eq 0 -and
        @($keptExpected|Where-Object {!$dictionary.ContainsKey($_)}).Count -eq 0) 'near_matching_and_unrelated_names_preserved'
    [Environment]::SetEnvironmentVariable($fakeName,$fakeValue,'Process')
    [Environment]::SetEnvironmentVariable($keepName,$keepValue,'Process')
    $start=New-CoreCoverageChildStartInfo -Directory (Join-Path $output 'construction-proof-not-executed')
    Require-CoreCoverageChild (!$start.StartInfo.Environment.ContainsKey($fakeName) -and
        $start.StartInfo.Environment[$keepName] -ceq $keepValue -and $fakeName -cin $start.RemovedEnvironmentNames) 'child_environment_isolated_by_name_without_values'
    Require-CoreCoverageChild ([Environment]::GetEnvironmentVariable($fakeName,'Process') -ceq $fakeValue -and
        [Environment]::GetEnvironmentVariable($keepName,'Process') -ceq $keepValue) 'synthetic_parent_values_preserved_after_construction'
    $result=Invoke-CoreCoverageChild -Directory $output
    Require-CoreCoverageChild ([Environment]::GetEnvironmentVariable($fakeName,'Process') -ceq $fakeValue -and
        [Environment]::GetEnvironmentVariable($keepName,'Process') -ceq $keepValue) 'synthetic_parent_values_preserved_after_execution'
    Require-CoreCoverageChild ($result.status -ceq 'PASS' -and $result.childExitCode -eq 0 -and
        !$result.parentEnvironmentModified -and !$result.stdoutLogged -and !$result.stderrLogged) 'all_existing_native_nine_atomic_two_and_real_collection_pass_in_one_child'
    foreach($name in @('powershell-coverage.xml','powershell-coverage-result.json','launcher-result.json','native-result.json')) {
        $content=[IO.File]::ReadAllText((Join-Path $output $name))
        Require-CoreCoverageChild (!$content.Contains($fakeValue,[StringComparison]::Ordinal) -and
            !$content.Contains($keepValue,[StringComparison]::Ordinal)) ('no_synthetic_value_in_'+$name.Replace('.','_'))
    }
    $proof.status='PASS';$proof.code='CORE_COVERAGE_CHILD_PROOF_PASSED';$proof.stage='complete'
} catch {
    $proof.code=if($_.Exception -is [InvalidOperationException] -and $_.Exception.Message -cmatch '^CORE_COVERAGE_[A-Z_]+$'){
        $_.Exception.Message
    }else{'CORE_COVERAGE_CHILD_PROOF_FAILED'}
} finally {
    [Environment]::SetEnvironmentVariable($fakeName,$null,'Process')
    [Environment]::SetEnvironmentVariable($keepName,$null,'Process')
    $proof.assertionsPassed=$checks.Count;$proof.assertions=$checks.ToArray()
    Save-CoreCoverageChildReceipt (Join-Path $output 'launcher-proof.json') $proof
}
$proof|ConvertTo-Json -Depth 4 -Compress
if($proof.status -ne 'PASS'){throw [InvalidOperationException]::new($proof.code)}
