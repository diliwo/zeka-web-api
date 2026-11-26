[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportFile)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $repo 'Tools/Zeka.AuthOwnerProvision/Prepare-OwnerHelperBuildContext.ps1') -DefinitionsOnly
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body){& $Body;$checks.Add(@{name=$Name;passed=$true})}
function Refused([string]$Code,[scriptblock]$Body){try{& $Body|Out-Null}catch{if($_.Exception-is[InvalidOperationException]-and$_.Exception.Message-ceq$Code){return};throw};throw "EXPECTED_GUARD_NOT_OBSERVED:$Code"}
$inventory=Get-OwnerHelperBuildInventory;$all=@($inventory.RuntimeFiles)+@($inventory.PublishOnlyFiles)
Check 'reviewed_31_runtime_files' {if($inventory.RuntimeFiles.Count-ne31){throw 'RUNTIME_INVENTORY_CHANGED'}}
Check 'reviewed_38_publish_outputs' {if($all.Count-ne38){throw 'PUBLISH_INVENTORY_CHANGED'}}
Check 'complete_publish_names_accepted' {Assert-OwnerHelperNames $all $all}
Check 'unexpected_file_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Assert-OwnerHelperNames ($all+@('forbidden.pem')) $all}}
Check 'missing_required_name_refused' {Refused 'OWNER_HELPER_REQUIRED_FILE_MISSING' {Assert-OwnerHelperNames $all[1..($all.Count-1)] $all}}
Check 'duplicate_name_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Assert-OwnerHelperNames ($all+@($all[0])) $all}}
Check 'traversal_name_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Assert-OwnerHelperNames @('../forbidden.pem') $all}}
Check 'wrong_case_name_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Assert-OwnerHelperNames @('AUTHMANAGER.APPLICATION.DLL') $all}}
$facts=@{SourceInsideRepository=$false;ContextInsideRepository=$false;SourceInsidePrivate=$false;ContextInsidePrivate=$false;SourceContextOverlap=$false;ReparseFound=$false}
Check 'admitted_path_facts' {Assert-OwnerHelperPathFacts $facts}
foreach($key in @($facts.Keys)){Check ('path_refuses_'+$key) {$bad=$facts.Clone();$bad[$key]=$true;Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {Assert-OwnerHelperPathFacts $bad}}}
Check 'relative_path_refused' {Refused 'OWNER_HELPER_PATH_REJECTED' {Get-OwnerHelperAbsolutePath 'relative/publish'}}
Check 'root_path_refused' {Refused 'OWNER_HELPER_PATH_REJECTED' {Get-OwnerHelperAbsolutePath ([IO.Path]::GetPathRoot([IO.Path]::GetTempPath()))}}
Check 'unc_path_refused' {Refused 'OWNER_HELPER_PATH_REJECTED' {Get-OwnerHelperAbsolutePath (([string][char]92)+[char]92+'unapproved-host'+[char]92+'publish')}}
$savedPrivateRoot=$env:ZEKA_M1_PRIVATE_DIR
try {
    $customPrivate=Join-Path ([IO.Path]::GetTempPath()) ('zeka-custom-private-'+[Guid]::NewGuid().ToString('N'))
    $customPublic=Join-Path ([IO.Path]::GetTempPath()) ('zeka-public-context-'+[Guid]::NewGuid().ToString('N'))
    $env:ZEKA_M1_PRIVATE_DIR=$customPrivate+[IO.Path]::DirectorySeparatorChar
    Check 'custom_private_trailing_separator_source_refused' {Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {
        Prepare-OwnerHelperBuildContext -PublishDirectory (Join-Path $customPrivate 'publish') -ContextDirectory $customPublic -SourceCommit ('a'*40) -BinarySha256 ('b'*64)}}
    Check 'custom_private_trailing_separator_context_refused' {Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {
        Prepare-OwnerHelperBuildContext -PublishDirectory $customPublic -ContextDirectory (Join-Path $customPrivate 'context') -SourceCommit ('a'*40) -BinarySha256 ('b'*64)}}
} finally {$env:ZEKA_M1_PRIVATE_DIR=$savedPrivateRoot}
$ignore=Get-OwnerHelperDockerIgnore;$ignoreLines=@($ignore.TrimEnd()-split"`n")
Check 'context_ignore_default_deny' {if($ignoreLines[0]-cne'**'){throw 'IGNORE_DEFAULT_NOT_DENY'}}
Check 'context_ignore_exact_allowlist' {Assert-OwnerHelperNames @($ignoreLines[1..($ignoreLines.Count-1)]|ForEach-Object {$_.Substring(1)}) (@($inventory.RuntimeFiles)+@($inventory.ControlFiles))}
Check 'publish_only_files_not_allowed_in_context' {foreach($name in $inventory.PublishOnlyFiles){if($ignoreLines-ccontains('!'+$name)){throw 'PUBLISH_ONLY_CONTEXT_ALLOW'}}}
$dockerfile=Join-Path $repo 'Tools/Zeka.AuthOwnerProvision/Dockerfile';$dockerText=[IO.File]::ReadAllText($dockerfile)
$normalized=$dockerText-replace'\\\r?\n\s*','';$copyStart=$normalized.IndexOf('COPY [');if($copyStart-lt0){throw 'EXPLICIT_COPY_MISSING'}
$jsonStart=$normalized.IndexOf('[',$copyStart);$jsonEnd=$normalized.IndexOf(']',$jsonStart);$copy=@($normalized.Substring($jsonStart,$jsonEnd-$jsonStart+1)|ConvertFrom-Json)
Check 'single_explicit_copy_instruction' {if(([regex]::Matches($dockerText,'(?m)^COPY ')).Count-ne1){throw 'UNREVIEWED_COPY_INSTRUCTION'}}
Check 'copy_destination_is_app' {if($copy[-1]-cne'/app/'){throw 'COPY_DESTINATION_CHANGED'}}
Check 'copy_exact_runtime_inventory' {Assert-OwnerHelperNames $copy[0..($copy.Count-2)] $inventory.RuntimeFiles}
Check 'copy_has_no_recursive_or_wildcard_source' {foreach($name in $copy[0..($copy.Count-2)]){if($name-match'[?*]' -or$name-eq'.'){throw 'BROAD_COPY_SOURCE'}}}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('zeka-owner-context-tests-'+[Guid]::NewGuid().ToString('N'))
$publish=Join-Path $fixture 'publish';[IO.Directory]::CreateDirectory($publish)|Out-Null
foreach($name in $all){[IO.File]::WriteAllText((Join-Path $publish $name),'synthetic fixture bytes: '+$name,[Text.UTF8Encoding]::new($false))}
$fixtureSha=('a'*40);$binaryHash=(Get-FileHash -LiteralPath (Join-Path $publish 'zeka-auth-owner-provision.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$context=Join-Path $fixture 'context'
Check 'dummy_fixture_preparation_accepted' {$script:receipt=Prepare-OwnerHelperBuildContext $publish $context $fixtureSha $binaryHash;if($receipt.status-cne'PREPARED'-or$receipt.runtimeFiles.Count-ne31){throw 'PREPARED_RECEIPT_INVALID'}}
Check 'prepared_context_exact_33_files' {Assert-OwnerHelperDirectoryFiles $context (@($inventory.RuntimeFiles)+@($inventory.ControlFiles))}
Check 'prepared_context_receipt_revalidated' {Assert-OwnerHelperPreparedContext $context $receipt}
Check 'prepared_context_excludes_symbols_and_apphost' {foreach($name in $inventory.PublishOnlyFiles){if(Test-Path -LiteralPath (Join-Path $context $name)){throw 'PUBLISH_ONLY_FILE_COPIED'}}}
Check 'existing_context_not_overwritten' {Refused 'OWNER_HELPER_CONTEXT_EXISTS' {Prepare-OwnerHelperBuildContext $publish $context $fixtureSha $binaryHash}}
Check 'bad_binary_hash_refused' {Refused 'OWNER_HELPER_BINARY_MISMATCH' {Prepare-OwnerHelperBuildContext $publish (Join-Path $fixture 'bad-hash') $fixtureSha ('0'*64)}}
Check 'invalid_source_id_refused' {Refused 'OWNER_HELPER_PROVENANCE_REJECTED' {Prepare-OwnerHelperBuildContext $publish (Join-Path $fixture 'bad-source') 'invalid' $binaryHash}}
Check 'overlapping_source_context_refused' {Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {Prepare-OwnerHelperBuildContext $publish (Join-Path $publish 'context') $fixtureSha $binaryHash}}
Check 'traversal_path_refused' {Refused 'OWNER_HELPER_PATH_REJECTED' {Prepare-OwnerHelperBuildContext (Join-Path $publish '../publish') (Join-Path $fixture 'traversal') $fixtureSha $binaryHash}}
Check 'repository_context_refused' {Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {Prepare-OwnerHelperBuildContext $publish (Join-Path $repo 'prohibited-context-fixture') $fixtureSha $binaryHash}}
Check 'private_path_pattern_refused' {Refused 'OWNER_HELPER_PATH_BOUNDARY_REJECTED' {Prepare-OwnerHelperBuildContext (Join-Path $fixture 'Zeka/M1/private/publish') (Join-Path $fixture 'private') $fixtureSha $binaryHash}}
$forbidden=Join-Path $publish 'forbidden.pem';[IO.File]::WriteAllText($forbidden,'SYNTHETIC FORBIDDEN MATERIAL - NOT A KEY',[Text.UTF8Encoding]::new($false))
Check 'synthetic_forbidden_publish_file_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Prepare-OwnerHelperBuildContext $publish (Join-Path $fixture 'forbidden') $fixtureSha $binaryHash}}
[IO.File]::Delete($forbidden)
$missing=Join-Path $publish 'AuthManager.Core.dll';$original=[IO.File]::ReadAllBytes($missing);[IO.File]::Delete($missing)
Check 'missing_runtime_file_refused' {Refused 'OWNER_HELPER_REQUIRED_FILE_MISSING' {Prepare-OwnerHelperBuildContext $publish (Join-Path $fixture 'missing') $fixtureSha $binaryHash}}
[IO.File]::WriteAllBytes($missing,$original)
$subdirectory=Join-Path $publish 'unreviewed-directory';[IO.Directory]::CreateDirectory($subdirectory)|Out-Null
Check 'publish_subdirectory_refused' {Refused 'OWNER_HELPER_NONREGULAR_FILE' {Prepare-OwnerHelperBuildContext $publish (Join-Path $fixture 'directory') $fixtureSha $binaryHash}}
[IO.Directory]::Delete($subdirectory)
$forbidden=Join-Path $context 'forbidden.pem';[IO.File]::WriteAllText($forbidden,'SYNTHETIC FORBIDDEN MATERIAL - NOT A KEY',[Text.UTF8Encoding]::new($false))
Check 'late_context_stray_file_refused' {Refused 'OWNER_HELPER_UNEXPECTED_FILE' {Assert-OwnerHelperPreparedContext $context $receipt}}
[IO.File]::Delete($forbidden)
$ignorePath=Join-Path $context '.dockerignore';[IO.File]::AppendAllText($ignorePath,"!forbidden.pem`n")
Check 'changed_ignore_policy_refused' {Refused 'OWNER_HELPER_CONTROL_FILE_CHANGED' {Assert-OwnerHelperPreparedContext $context $receipt}}
[IO.File]::WriteAllText($ignorePath,$ignore,[Text.UTF8Encoding]::new($false))
$changed=Join-Path $context 'AuthManager.Core.dll';[IO.File]::AppendAllText($changed,'changed')
Check 'changed_runtime_bytes_refused' {Refused 'OWNER_HELPER_FILE_CHANGED' {Assert-OwnerHelperPreparedContext $context $receipt}}
$report=[ordered]@{schema='zeka-m1-owner-helper-context-tests/v1';status='PASS';count=$checks.Count;checks=$checks;realAssembliesUsed=$false;dockerExecuted=$false;imageFilesystemInspected=$false;fixtureRetained=$fixture}
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($ReportFile));if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'REPORT_DIRECTORY_REQUIRED'}
if(Test-Path -LiteralPath $ReportFile){throw 'REPORT_EXISTS'};$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $ReportFile -Encoding utf8NoBOM
$report|ConvertTo-Json -Depth 6 -Compress
