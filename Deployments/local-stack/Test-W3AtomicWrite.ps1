[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'W3-NativeDiagnostics.ps1')
$directoryPath=[IO.Path]::GetFullPath($Directory)
if(Test-Path -LiteralPath $directoryPath){throw 'TEST_DIRECTORY_EXISTS'}
[IO.Directory]::CreateDirectory($directoryPath)|Out-Null
$destination=Join-Path $directoryPath 'state.json'
$temporary=Join-Path $directoryPath '.first.tmp'
[IO.File]::WriteAllText($temporary,'first')
Complete-W3AtomicFileWrite -TemporaryPath $temporary -DestinationPath $destination
if([IO.File]::ReadAllText($destination)-ne'first'-or(Test-Path -LiteralPath $temporary)){throw 'INITIAL_ATOMIC_MOVE_FAILED'}
$temporary=Join-Path $directoryPath '.second.tmp'
[IO.File]::WriteAllText($temporary,'second')
Complete-W3AtomicFileWrite -TemporaryPath $temporary -DestinationPath $destination -Replace
if([IO.File]::ReadAllText($destination)-ne'second'-or(Test-Path -LiteralPath $temporary)){throw 'ATOMIC_REPLACE_FAILED'}
$result=@{status='PASS';passed=2;failed=0;actualHelper='Complete-W3AtomicFileWrite';cases=@('initial_same_directory_move','existing_destination_replace_with_true_null_backup');data='nonsecret synthetic marker text'}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directoryPath 'atomic-write-result.json')
Write-Output ($result|ConvertTo-Json -Compress)
