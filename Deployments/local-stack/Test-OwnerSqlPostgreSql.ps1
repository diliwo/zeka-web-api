# Disposable provider fixture only. Never targets an existing database/container/volume.
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$CaseFile,
 [Parameter(Mandatory)][string]$EvidenceDirectory,
 [string]$SourceRoot=(Join-Path $PSScriptRoot '../..'),
 [string]$Sonar='sonar',
 [string]$PostgresImage='postgres:17-bookworm',
 [string]$RuntimeImage='mcr.microsoft.com/dotnet/aspnet:8.0.29-bookworm-slim'
)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($SourceRoot);$output=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $output){throw 'FRESH_EVIDENCE_DIRECTORY_REQUIRED'}
if($output.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'EVIDENCE_INSIDE_SOURCE_REJECTED'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$public=Join-Path $output 'public';$work=Join-Path $output 'fixture-work'
[IO.Directory]::CreateDirectory($public)|Out-Null;[IO.Directory]::CreateDirectory($work)|Out-Null
$native=[Collections.Generic.List[object]]::new();$fixture=$null;$runtimeContainer=$null;$stage='source-admission';$status='FAIL'
$sha=$null;$case=$null;$caseHash=$null;$marker=$null;$pgImage=$null;$runtimeId=$null;$sdk=$null;$protocolMarker=$null
$sanitizerReady=$false
function ConvertTo-OrchestratorSafeOutput([string]$Text){
 if(!$Text){return ''}
 if(!$script:sanitizerReady){
  # Do not load an unscanned sanitizer merely to report failure of its own admission.
  $category=if($Text-match'keychain|credential manager'){'SCANNER_KEYCHAIN_UNAVAILABLE'}elseif($Text-match'(?i)authenticat|unauthoriz|token|401|403'){'SCANNER_AUTHENTICATION_UNAVAILABLE'}else{'SANITIZER_NOT_ADMITTED'}
  return ([ordered]@{availability='WITHHELD_BEFORE_SANITIZER_SOURCE_ADMISSION';category=$category}|ConvertTo-Json -Compress)
 }
 $safe=$null
 try{
  $value=ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 30 -ErrorAction Stop
  if($value-is[Collections.IDictionary]){
   $projection=[ordered]@{}
   foreach($key in @('schema','status','stage','code','queryId','caseId','sqlState','exceptionType')){
    if($value.Contains($key)-and$value[$key]-is[string]-and$value[$key]-cmatch'\A[A-Za-z0-9._/-]{1,200}\z'){$projection[$key]=$value[$key]}
   }
   if($value.Contains('postgres')-and$null-ne$value.postgres){
    $d=ConvertFrom-SafePostgresDiagnostic ($value.postgres|ConvertTo-Json -Depth 8 -Compress)
    $d.message=ConvertTo-W3SafeText $d.message -StructuredPostgresField
    if($null-ne$d.hint){$d.hint=ConvertTo-W3SafeText $d.hint -StructuredPostgresField};$projection.postgres=$d
   }
   foreach($key in @('message','error')){if($value.Contains($key)-and$value[$key]-is[string]){$projection[$key]=ConvertTo-W3SafeText $value[$key]}}
   if($projection.Count-eq0){$projection.availability='UNRECOGNIZED_PROTOCOL_FIELDS_WITHHELD'}
   $safe=$projection|ConvertTo-Json -Depth 10 -Compress
  }
 }catch{if($Text.TrimStart().StartsWith('{')){$safe='{"availability":"MALFORMED_PROTOCOL_OUTPUT_WITHHELD"}'}}
 if($null-eq$safe){$safe=ConvertTo-W3SafeText $Text}
 foreach($pair in @(@($output,'[EVIDENCE_ROOT]'),@($root,'[SOURCE_ROOT]'),@([Environment]::GetFolderPath('UserProfile'),'[USER_PROFILE]'))){if($pair[0]){$safe=$safe.Replace($pair[0],$pair[1],[StringComparison]::OrdinalIgnoreCase)}}
 $safe=[regex]::Replace($safe,'(?i)[a-z]:\\Users\\[^\\\s"'']+','[USER_PROFILE]')
 $safe=[regex]::Replace($safe,'/(?:home|Users)/[^/\s"'']+','[USER_PROFILE]')
 if($safe.Length-gt16384){$safe=$safe.Substring(0,16384)+'[TRUNCATED]'}
 return $safe
}
function Invoke-CheckedNative([string]$Name,[string]$File,[string[]]$Arguments,[int]$Seconds=600,[switch]$AllowFailure){
 $info=[Diagnostics.ProcessStartInfo]::new($File);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.WorkingDirectory=$root
 foreach($a in $Arguments){$info.ArgumentList.Add($a)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$info;$watch=[Diagnostics.Stopwatch]::StartNew()
 $record=[ordered]@{operation=$Name;nativeStarted=$false;exitCode=$null;stdoutBytes=$null;stderrBytes=$null;elapsedMilliseconds=$null;timedOut=$false;stdoutSafe=$null;stderrSafe=$null;failureType=$null}
 $native.Add($record)
 $stdout=$null;$stderr=$null
 try{
  if(!$p.Start()){throw 'NATIVE_START_FAILED'};$record.nativeStarted=$true;$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync()
  if(!$p.WaitForExit($Seconds*1000)){$record.timedOut=$true;try{$p.Kill($true);$null=$p.WaitForExit(5000)}catch{};throw 'NATIVE_DEADLINE'}
  $o=$stdout.GetAwaiter().GetResult();$e=$stderr.GetAwaiter().GetResult();$code=$p.ExitCode
  $record.exitCode=$code;$record.stdoutBytes=[Text.Encoding]::UTF8.GetByteCount($o);$record.stderrBytes=[Text.Encoding]::UTF8.GetByteCount($e)
  if($code-ne0){$record.stdoutSafe=ConvertTo-OrchestratorSafeOutput $o;$record.stderrSafe=ConvertTo-OrchestratorSafeOutput $e}
  if($code-ne0-and!$AllowFailure){throw ('NATIVE_FAILED_'+$Name)}
  return [pscustomobject]@{ExitCode=$code;Output=$o}
 }catch{
  $record.failureType=$_.Exception.GetType().Name
  if(!$record.stderrSafe){
   if($null-ne$stderr-and$stderr.IsCompletedSuccessfully){$record.stderrSafe=ConvertTo-OrchestratorSafeOutput $stderr.GetAwaiter().GetResult()}
   else{$record.stderrSafe=ConvertTo-OrchestratorSafeOutput $_.Exception.Message}
  }
  if(!$record.stdoutSafe-and$null-ne$stdout-and$stdout.IsCompletedSuccessfully){$record.stdoutSafe=ConvertTo-OrchestratorSafeOutput $stdout.GetAwaiter().GetResult()}
  throw
 }finally{$record.elapsedMilliseconds=$watch.ElapsedMilliseconds;$p.Dispose()}
}
function Read-ScannedText([string]$Path){
 $null=Invoke-CheckedNative 'source-secret-admission' $Sonar @('analyze','secrets',[IO.Path]::GetFullPath($Path)) 120
 return [IO.File]::ReadAllText($Path)
}
function Write-PublicJson([string]$Name,$Value){[IO.File]::WriteAllText((Join-Path $public $Name),($Value|ConvertTo-Json -Depth 50),[Text.UTF8Encoding]::new($false))}
function Copy-SafeFile([string]$Relative,[string]$Hash){
 if([IO.Path]::IsPathRooted($Relative)-or$Relative.Contains('\')-or$Relative.Split('/')-contains'..'){throw 'SOURCE_PATH_REJECTED'}
 $source=Join-Path $root $Relative;$null=Read-ScannedText $source
 if((Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()-cne$Hash){throw 'SOURCE_HASH_CHANGED'}
 $target=Join-Path (Join-Path $work 'source') $Relative
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::Copy($source,$target,$false)
}
try{
 foreach($safeSource in @('SafePostgresDiagnostics.ps1','W3-NativeDiagnostics.ps1')){$null=Read-ScannedText (Join-Path $PSScriptRoot $safeSource)}
 . (Join-Path $PSScriptRoot 'W3-NativeDiagnostics.ps1');$script:sanitizerReady=$true
 foreach($name in @('SONARQUBE_CLI_TOKEN','PACKAGE_READ_TOKEN','NuGetPackageSourceCredentials_github')){Register-W3Secret ([Environment]::GetEnvironmentVariable($name,'Process'))}
 $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
 $docker=(Get-Command docker -CommandType Application -ErrorAction Stop).Source
 $python=(Get-Command python -CommandType Application -ErrorAction Stop).Source
 $sdk=(Invoke-CheckedNative 'sdk-identity' $dotnet @('--version') 30).Output.Trim()
 if($sdk-cne'8.0.129'){throw 'PINNED_SDK_REQUIRED'}
 $sha=(Invoke-CheckedNative 'source-head' git @('rev-parse','HEAD') 30).Output.Trim()
 if($sha-cnotmatch'\A[0-9a-f]{40}\z'){throw 'SOURCE_SHA_REJECTED'}
 if((Invoke-CheckedNative 'source-clean' git @('status','--porcelain=v1','--untracked-files=all') 30).Output.Trim()){throw 'CLEAN_SOURCE_REQUIRED'}
 $case=Read-ScannedText ([IO.Path]::GetFullPath($CaseFile))|ConvertFrom-Json -Depth 50
 if($case.schema-cne'zeka-owner-sql-corpus/v1'-or$case.sourceCommit-cnotmatch'\A[0-9a-f]{40}\z'-or$case.queries.Count-ne8-or$case.transactions.Count-ne4){throw 'CASE_SCHEMA_REJECTED'}
 $protocolMarker=$case.sourceCommit
 # A case file is a reviewed synthetic input. Its protocol source marker is NOT relabelled as Git HEAD.
 $caseHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $CaseFile).Hash.ToLowerInvariant()
 $marker='zeka-m1-bootstrap-fixture-'+[Guid]::NewGuid().ToString('N');$case.fixtureId=$marker
 $casePath=Join-Path $work 'cases.json';[IO.File]::WriteAllText($casePath,($case|ConvertTo-Json -Depth 50),[Text.UTF8Encoding]::new($false))
 $inventoryScript=Join-Path $root 'Tools/Shared/OwnerSqlInventory.py';$null=Read-ScannedText $inventoryScript
 $inventoryPath=Join-Path $public 'source-inventory.json'
 $null=Invoke-CheckedNative 'source-inventory' $python @($inventoryScript,'--source-root',$root,'--sonar',$Sonar,'--inventory-out',$inventoryPath) 300
 $inventory=Read-ScannedText $inventoryPath|ConvertFrom-Json -Depth 50
 if($inventory.producers.Count-lt78){throw 'PRODUCER_INVENTORY_SHRANK'}
 foreach($binding in $inventory.sourceBindings.PSObject.Properties){Copy-SafeFile $binding.Name $binding.Value}
 foreach($query in $case.queries){
  if($query.sourcePath-cne'Deployments/local-stack/OwnerFirstAdminPreflightQueries.ps1'-or$query.sourceSha256-cne$inventory.sourceBindings.($query.sourcePath)){throw 'CASE_FACTORY_SOURCE_MISMATCH'}
 }
 foreach($tx in $case.transactions){if($tx.sourceSha256-cne$inventory.sourceBindings.($tx.sourcePath)){throw 'CASE_TRANSACTION_SOURCE_MISMATCH'}}
 $stage='publish-five-tools';$publish=Join-Path $work 'publish';[IO.Directory]::CreateDirectory($publish)|Out-Null
 $projects=[ordered]@{setup='Zeka.LocalStack.DatabaseSetup';migrator='Zeka.DbMigrate';owner='Zeka.AuthOwnerProvision';probe='Zeka.OwnerSqlProbe';corpus='Zeka.OwnerSqlContractTests'}
 foreach($entry in $projects.GetEnumerator()){
  $project=Join-Path $root ('Tools/'+$entry.Value+'/'+$entry.Value+'.csproj');$null=Read-ScannedText $project
  $null=Invoke-CheckedNative ('restore-'+$entry.Key) $dotnet @('restore',$project,'--configfile',(Join-Path $root 'nuget.config')) 600
  $null=Invoke-CheckedNative ('publish-'+$entry.Key) $dotnet @('publish',$project,'--no-restore','--configuration','Release','--output',(Join-Path $publish $entry.Key),'-p:DebugType=portable',('-p:ZekaSourceSha='+$case.sourceCommit)) 600
 }
 foreach($name in @('setup','migrator','owner','probe')){if(!(Get-ChildItem -LiteralPath (Join-Path $publish $name) -Filter '*.pdb' -File)){throw 'PORTABLE_PDB_REQUIRED'}}
 $stage='fresh-fixture-create'
 $null=Invoke-CheckedNative 'pull-postgres17' $docker @('pull',$PostgresImage) 300
 $null=Invoke-CheckedNative 'pull-runtime' $docker @('pull',$RuntimeImage) 300
 $pgImage=(Invoke-CheckedNative 'bind-postgres-image' $docker @('image','inspect','--format','{{.Id}}',$PostgresImage) 30).Output.Trim()
 $runtimeId=(Invoke-CheckedNative 'bind-runtime-image' $docker @('image','inspect','--format','{{.Id}}',$RuntimeImage) 30).Output.Trim()
 if($pgImage-cnotmatch'^sha256:[0-9a-f]{64}$'-or$runtimeId-cnotmatch'^sha256:[0-9a-f]{64}$'){throw 'IMAGE_IDENTITY_REJECTED'}
 $runtimeContainer=(Invoke-CheckedNative 'create-runtime-export' $docker @('create','--network','none','--label',('com.diliwo.owner-sql.fixture='+$marker),$runtimeId,'dotnet','--info') 30).Output.Trim()
 $runtimePath=Join-Path $work 'dotnet';[IO.Directory]::CreateDirectory($runtimePath)|Out-Null
 $null=Invoke-CheckedNative 'copy-public-runtime' $docker @('cp',($runtimeContainer+':/usr/share/dotnet/.'),$runtimePath) 120
 $fixture=(Invoke-CheckedNative 'create-fresh-postgres' $docker @('create','--network','none','--add-host','postgres:127.0.0.1','--add-host','postgres-guard:127.0.0.1','--label',('com.diliwo.owner-sql.fixture='+$marker),'--env','POSTGRES_HOST_AUTH_METHOD=trust','--mount','type=tmpfs,destination=/var/lib/postgresql/data,tmpfs-size=1073741824',$pgImage,'-c','listen_addresses=127.0.0.1','-c','unix_socket_directories=/tmp','-c','log_statement=none','-c','log_min_error_statement=panic','-c','hba_file=/tmp/owner-sql-fixture.pg_hba.conf') 30).Output.Trim()
 if($fixture-cnotmatch'^[0-9a-f]{64}$'-or$runtimeContainer-cnotmatch'^[0-9a-f]{64}$'){throw 'FRESH_CONTAINER_ID_REJECTED'}
 $hba=Join-Path $work 'pg_hba.conf';[IO.File]::WriteAllText($hba,"local all all trust`nhost all all 127.0.0.1/32 scram-sha-256`nhost all all 0.0.0.0/0 reject`nhost all all ::/0 reject`n",[Text.UTF8Encoding]::new($false))
 $init=Join-Path $work 'init.sql';[IO.File]::WriteAllText($init,("COMMENT ON DATABASE postgres IS '"+$marker+"';`n"),[Text.UTF8Encoding]::new($false))
 foreach($copy in @(@($hba,'/tmp/owner-sql-fixture.pg_hba.conf'),@($init,'/docker-entrypoint-initdb.d/01-owner-sql-fixture.sql'),@($runtimePath,'/tmp/dotnet'),@((Join-Path $work 'source'),'/tmp/source'),@($publish,'/tmp/tools'),@($casePath,'/tmp/cases.json'))){$null=Invoke-CheckedNative 'copy-public-fixture-input' $docker @('cp',$copy[0],($fixture+':'+$copy[1])) 120}
 $null=Invoke-CheckedNative 'start-fresh-postgres' $docker @('start',$fixture) 30
 $ready=$false
 for($attempt=0;$attempt-lt30;$attempt++){
  $probe=Invoke-CheckedNative 'wait-fresh-postgres' $docker @('exec',$fixture,'pg_isready','-h','/tmp','-U','postgres','-d','postgres') 10 -AllowFailure
  if($probe.ExitCode-eq0){$ready=$true;break};Start-Sleep -Seconds 1
 }
 if(!$ready){throw 'FRESH_POSTGRES_NOT_READY'}
 $null=Invoke-CheckedNative 'admit-public-runtime-executable' $docker @('exec','--user','root',$fixture,'chmod','755','/tmp/dotnet/dotnet') 30
 $null=Invoke-CheckedNative 'create-public-receipt-directory' $docker @('exec','--user','root',$fixture,'mkdir','-m','700','/tmp/sql-receipts') 30
 $stage='real-pg17-corpus'
 $corpus=Invoke-CheckedNative 'real-pg17-corpus' $docker @('exec','--user','root','--env','ZEKA_OWNER_SQL_EVIDENCE=1','--env','ZEKA_OWNER_SQL_EVIDENCE_DIR=/tmp/sql-receipts','--env','ZEKA_OWNER_SQL_SOURCE_ROOT=/tmp/source',$fixture,'/tmp/dotnet/dotnet','/tmp/tools/corpus/Zeka.OwnerSqlContractTests.dll','--dotnet','/tmp/dotnet/dotnet','--setup','/tmp/tools/setup/zeka-local-db-setup.dll','--migrator','/tmp/tools/migrator/zeka-db-migrate.dll','--owner','/tmp/tools/owner/zeka-auth-owner-provision.dll','--probe','/tmp/tools/probe/zeka-owner-sql-probe.dll','--source-root','/tmp/source','--case-file','/tmp/cases.json','--evidence-root','/tmp/corpus-evidence','--fixture-host','/tmp','--fixture-port','5432') 1260 -AllowFailure
 $receiptPath=Join-Path $public 'sql-receipts';[IO.Directory]::CreateDirectory($receiptPath)|Out-Null
 $null=Invoke-CheckedNative 'collect-public-sql-receipts' $docker @('cp',($fixture+':/tmp/sql-receipts/.'),$receiptPath) 30
 $null=Invoke-CheckedNative 'collect-public-corpus-result' $docker @('cp',($fixture+':/tmp/corpus-evidence/corpus-result.json'),(Join-Path $public 'corpus-result.json')) 30 -AllowFailure
 $stage='compare-every-producer'
 $comparison=Invoke-CheckedNative 'compare-every-producer' $python @($inventoryScript,'--source-root',$root,'--sonar',$Sonar,'--receipts',$receiptPath,'--report',(Join-Path $public 'owner-sql-coverage.json')) 300 -AllowFailure
 if($corpus.ExitCode-ne0-or$comparison.ExitCode-ne0){throw 'REAL_PROVIDER_CORPUS_OR_COVERAGE_FAILED'}
 $status='PASS';$stage='completed'
}finally{
 Write-PublicJson 'native-status.json' ($native.ToArray())
 Write-PublicJson 'orchestrator-result.json' ([ordered]@{schema='zeka-owner-sql-orchestrator/v1';status=$status;stage=$stage;actualGitHead=$sha;sdk=$sdk;protocolFixtureSourceMarker=$protocolMarker;inputCaseSha256=$caseHash;fixtureId=$marker;fixtureContainer=$fixture;runtimeExportContainer=$runtimeContainer;postgresImage=$pgImage;runtimeImage=$runtimeId;protectedM1='NOT_USED';cleanup='NOT_RUN_PRESERVED';scope='Current inventoried handwritten producers only; generated migrations and PLpgSQL branch semantics remain separate';caseGeneration='REVIEWED_CASE_FILE_INPUT; fresh source-bound model/factory generation remains a separate CI admission dependency'})
 # No automatic cleanup: retain this newly owned disposable fixture and original failure evidence.
 # CI uploads ONLY public/*, never fixture-work, binaries, raw source, SQL files or protected material.
}
