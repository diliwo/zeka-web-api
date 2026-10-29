[CmdletBinding()]
param([string]$ReportFile)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../../Services/AuthManager/Tests/Infrastructure.IntegrationTests/Configuration/Test-Configuration.ps1') @PSBoundParameters
exit $LASTEXITCODE
