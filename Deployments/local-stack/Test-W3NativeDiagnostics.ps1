[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../../Tools/Zeka.DbMigrate.Tests/LocalStackConfiguration/Test-W3NativeDiagnostics.ps1') @PSBoundParameters
exit $LASTEXITCODE
