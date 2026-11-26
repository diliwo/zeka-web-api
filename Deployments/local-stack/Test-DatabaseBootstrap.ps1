[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$FixtureDirectory = (Join-Path $env:LOCALAPPDATA 'Zeka\M1\bootstrap-fixtures'),
    [ValidatePattern('^[a-f0-9]{32}$')][string]$FixtureIdentity = [Guid]::NewGuid().ToString('N')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../../Tools/Zeka.DbMigrate.Tests/LocalStackConfiguration/Test-DatabaseBootstrap.ps1') @PSBoundParameters
exit $LASTEXITCODE
